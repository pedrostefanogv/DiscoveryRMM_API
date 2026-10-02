using Discovery.Api.DependencyInjection;
using Discovery.Api.Services.Quartz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;

namespace Discovery.Tests;

/// <summary>
/// Contrato de nomes do scheduler: o <c>JobKey</c> do Quartz é derivado da
/// identidade do trigger pelo <c>ScheduleJob&lt;T&gt;</c>. Trigger e job devem
/// usar o MESMO nome base declarado pelo job — senão o acionamento manual em
/// /api/v1/admin/jobs/{grupo}/{nome}/trigger devolve 404 e o /status reporta um
/// nome que não existe no scheduler.
/// </summary>
public class QuartzRegistrationContractTests
{
    private static async Task<IScheduler> BuildSchedulerAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDiscoveryQuartz(new ConfigurationBuilder().Build());

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISchedulerFactory>();
        return await factory.GetScheduler();
    }

    [Test]
    public async Task JobsAreRegisteredWithTheirBaseKey()
    {
        var scheduler = await BuildSchedulerAsync();
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());

        JobKey[] expected =
        [
            TechnicianMetricsRefreshJob.Key,
            AiTicketAssignmentJob.Key,
            TechnicianMetricsBackfillJob.Key,
            BackgroundProcessingScheduleSyncJob.Key,
            KnowledgeEmbeddingJob.Key,
            TicketAnswerEmbeddingJob.Key,
            SlaMonitoringJob.Key,
            AlertSchedulerJob.Key,
            AgentLabelingReconciliationJob.Key,
            ReportGenerationJob.Key,
            ReportScheduleDispatchJob.Key,
            LogPurgeJob.Key,
            DataRetentionJob.Key,
            AiChatRetentionJob.Key,
            DatabaseMaintenanceJob.Key,
            P2pMaintenanceJob.Key,
        ];

        foreach (var key in expected)
        {
            Assert.That(
                keys.Any(k => k.Name == key.Name && k.Group == key.Group),
                Is.True,
                $"Job {key.Group}.{key.Name} não foi registrado com o nome base.");
        }
    }

    [Test]
    public async Task NoJobKeyCarriesTheLegacyTriggerSuffix()
    {
        var scheduler = await BuildSchedulerAsync();
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());

        Assert.That(
            keys.Where(k => k.Name.EndsWith("-trigger", StringComparison.Ordinal)).Select(k => k.ToString()),
            Is.Empty,
            "JobKey não deve carregar o sufixo -trigger (causa histórica do 404).");
    }

    [Test]
    public async Task RescheduleFindsTheTriggerByItsBaseName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDiscoveryQuartz(new ConfigurationBuilder().Build());

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await factory.GetScheduler();
        var rescheduler = new Discovery.Api.Services.BackgroundProcessing.QuartzBackgroundProcessingScheduler(factory);

        var next = await rescheduler.RescheduleAsync(
            TechnicianMetricsRefreshJob.Key.Name, TechnicianMetricsRefreshJob.Key.Group, 300, 0);

        Assert.That(next, Is.Not.Null, "o tick só é reprogramado se o trigger existir com o nome base");
        Assert.That(
            await rescheduler.GetNextFireTimeAsync(TechnicianMetricsRefreshJob.Key.Name, TechnicianMetricsRefreshJob.Key.Group),
            Is.Not.Null);
    }

    [Test]
    public async Task TriggerAndJobShareTheSameName()
    {
        var scheduler = await BuildSchedulerAsync();
        var triggers = await scheduler.GetTriggersOfJob(TechnicianMetricsRefreshJob.Key);

        Assert.That(triggers, Is.Not.Empty);
        Assert.That(
            triggers.Select(t => t.Key.Name),
            Is.EqualTo(new[] { TechnicianMetricsRefreshJob.Key.Name }));
        Assert.That(
            triggers.Select(t => t.Key.Group),
            Is.EqualTo(new[] { TechnicianMetricsRefreshJob.Key.Group }));
    }
}
