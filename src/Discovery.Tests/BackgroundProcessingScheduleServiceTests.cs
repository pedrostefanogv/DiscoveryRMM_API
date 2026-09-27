using Discovery.Api.Services.BackgroundProcessing;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Reprogramação dinâmica do tick a partir da configuração (banco): aplica o
/// valor desejado, não reprograma sem mudança, respeita o kill switch e reporta
/// quando o job não está agendado.
/// </summary>
public class BackgroundProcessingScheduleServiceTests
{
    private const string MetricsJob = "technician-metrics-refresh";
    private const string TriageJob = "ai-ticket-assignment";

    private static BackgroundProcessingSettings Settings(int metricsTick, int triageTick)
    {
        var settings = new BackgroundProcessingSettings();
        settings.Metrics.TickSeconds = metricsTick;
        settings.Triage.TickSeconds = triageTick;
        return settings;
    }

    private static BackgroundProcessingScheduleService BuildService(
        FakeScheduler scheduler, FakeConfigResolver resolver, Dictionary<string, string?>? appSettings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(appSettings ?? [])
            .Build();

        return new BackgroundProcessingScheduleService(
            scheduler, resolver, configuration, NullLogger<BackgroundProcessingScheduleService>.Instance);
    }

    [Test]
    public async Task Apply_UsesDatabaseTick_AndDoesNotRescheduleWithoutChange()
    {
        var scheduler = new FakeScheduler();
        var resolver = new FakeConfigResolver(Settings(600, 45));
        var service = BuildService(scheduler, resolver);

        var first = await service.ApplyAsync();

        Assert.That(scheduler.Reschedules, Has.Count.EqualTo(2));
        Assert.That(scheduler.Reschedules.Any(r => r.Job == MetricsJob && r.Interval == 600), Is.True);
        Assert.That(scheduler.Reschedules.Any(r => r.Job == TriageJob && r.Interval == 45), Is.True);
        Assert.That(first.Processes.All(p => p.AppliedTickSeconds == p.TickSeconds), Is.True);

        await service.ApplyAsync();
        Assert.That(scheduler.Reschedules, Has.Count.EqualTo(2), "sem mudança não reprograma");
    }

    [Test]
    public async Task Apply_ReprogramsWhenTickChanges()
    {
        var scheduler = new FakeScheduler();
        var resolver = new FakeConfigResolver(Settings(600, 45));
        var service = BuildService(scheduler, resolver);

        await service.ApplyAsync();
        resolver.Settings = Settings(300, 45);

        await service.ApplyAsync();

        Assert.That(scheduler.Reschedules, Has.Count.EqualTo(3));
        Assert.That(scheduler.Reschedules.Last().Interval, Is.EqualTo(300));
    }

    [Test]
    public async Task Apply_WithForce_ReprogramsEvenWithoutChange()
    {
        var scheduler = new FakeScheduler();
        var resolver = new FakeConfigResolver(Settings(600, 45));
        var service = BuildService(scheduler, resolver);

        await service.ApplyAsync();
        await service.ApplyAsync(force: true);

        Assert.That(scheduler.Reschedules, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task Apply_RespectsKillSwitch()
    {
        var scheduler = new FakeScheduler();
        var resolver = new FakeConfigResolver(Settings(600, 45));
        var service = BuildService(scheduler, resolver, new Dictionary<string, string?>
        {
            ["BackgroundJobs:TechnicianMetrics:Enabled"] = "false"
        });

        var snapshot = await service.ApplyAsync();

        Assert.That(scheduler.Reschedules.Any(r => r.Job == MetricsJob), Is.False);
        Assert.That(scheduler.Reschedules.Any(r => r.Job == TriageJob), Is.True);

        var metrics = snapshot.Processes.Single(p => p.JobName == MetricsJob);
        Assert.That(metrics.Enabled, Is.False);
    }

    [Test]
    public async Task Apply_WhenJobIsNotScheduled_ReportsDisabledWithoutFailure()
    {
        var scheduler = new FakeScheduler { JobScheduled = false };
        var resolver = new FakeConfigResolver(Settings(600, 45));
        var service = BuildService(scheduler, resolver);

        var snapshot = await service.ApplyAsync();

        Assert.That(snapshot.Processes.All(p => !p.Enabled), Is.True);
        Assert.That(snapshot.Processes.All(p => p.NextFireTimeUtc is null), Is.True);
    }

    [Test]
    public async Task GetStatus_ReadsWithoutRescheduling()
    {
        var scheduler = new FakeScheduler();
        var resolver = new FakeConfigResolver(Settings(600, 45));
        var service = BuildService(scheduler, resolver);

        var snapshot = await service.GetStatusAsync();

        Assert.That(scheduler.Reschedules, Is.Empty);
        Assert.That(snapshot.Processes, Has.Count.EqualTo(2));
        Assert.That(snapshot.Processes.All(p => p.NextFireTimeUtc is not null), Is.True);
    }

    private sealed class FakeScheduler : IBackgroundProcessingScheduler
    {
        public List<(string Job, int Interval)> Reschedules { get; } = [];
        public bool JobScheduled { get; set; } = true;
        public DateTimeOffset? NextFire { get; set; } = DateTimeOffset.UtcNow.AddMinutes(1);

        public Task<DateTimeOffset?> RescheduleAsync(
            string jobName, string jobGroup, int intervalSeconds, int startupDelaySeconds,
            CancellationToken ct = default)
        {
            if (!JobScheduled) return Task.FromResult<DateTimeOffset?>(null);

            Reschedules.Add((jobName, intervalSeconds));
            return Task.FromResult(NextFire);
        }

        public Task<DateTimeOffset?> GetNextFireTimeAsync(
            string jobName, string jobGroup, CancellationToken ct = default)
            => Task.FromResult(JobScheduled ? NextFire : null);
    }

    private sealed class FakeConfigResolver(BackgroundProcessingSettings settings) : IConfigurationResolver
    {
        public BackgroundProcessingSettings Settings { get; set; } = settings;

        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default)
            => Task.FromResult(Settings);

        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null) => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
        public void ClearCache() { }
    }
}
