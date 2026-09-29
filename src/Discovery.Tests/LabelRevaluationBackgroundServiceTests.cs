using Discovery.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Gatilho de reavaliacao por sync de inventario: desabilitado por padrao, com
/// coalescencia (debounce) por agente quando habilitado.
/// </summary>
public class LabelRevaluationBackgroundServiceTests
{
    [Test]
    public void Schedule_WhenDisabled_IsNoOp()
    {
        var service = CreateService();

        Assert.That(service.IsEnabled, Is.False, "O gatilho deve vir desabilitado por padrao.");

        service.Schedule(Guid.NewGuid());

        Assert.That(service.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void Schedule_WhenEnabled_CoalescesByAgent()
    {
        var service = CreateService(
            (LabelRevaluationBackgroundService.EnabledConfigKey, "true"),
            (LabelRevaluationBackgroundService.DebounceConfigKey, "10"));

        var agentId = Guid.NewGuid();
        service.Schedule(agentId);
        service.Schedule(agentId);
        service.Schedule(Guid.NewGuid());
        service.Schedule(Guid.Empty);

        Assert.That(service.IsEnabled, Is.True);
        Assert.That(service.PendingCount, Is.EqualTo(2),
            "Syncs repetidos do mesmo agente contam como um; Guid.Empty e ignorado.");
        Assert.That(service.DebounceInterval, Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void DebounceInterval_IsClampedToSafeRange()
    {
        var tooSmall = CreateService(
            (LabelRevaluationBackgroundService.EnabledConfigKey, "true"),
            (LabelRevaluationBackgroundService.DebounceConfigKey, "1"));
        var tooLarge = CreateService(
            (LabelRevaluationBackgroundService.EnabledConfigKey, "true"),
            (LabelRevaluationBackgroundService.DebounceConfigKey, "9999"));

        Assert.That(tooSmall.DebounceInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(tooLarge.DebounceInterval, Is.EqualTo(TimeSpan.FromSeconds(300)));
    }

    private static LabelRevaluationBackgroundService CreateService(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

        return new LabelRevaluationBackgroundService(
            new UnusedServiceProvider(),
            configuration,
            NullLogger<LabelRevaluationBackgroundService>.Instance);
    }

    private sealed class UnusedServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
