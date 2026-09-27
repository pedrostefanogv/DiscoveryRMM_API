using Discovery.Core.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Merge global → cliente e normalização de faixas: o cliente sobrepõe apenas
/// os campos escolhidos e NÃO altera o tick (um trigger é global).
/// </summary>
public class BackgroundProcessingSettingsTests
{
    [Test]
    public void Merge_AppliesClientOverrideFieldByField()
    {
        var global = new BackgroundProcessingSettings();
        var client = new BackgroundProcessingSettingsOverride
        {
            Metrics = new TechnicianMetricsProcessingOverride { IntervalMinutes = 60 },
            Triage = new TicketTriageProcessingOverride { EnqueueOnCreate = false }
        };

        var merged = BackgroundProcessingSettingsMerger.Merge(global, client);

        Assert.That(merged.Metrics.IntervalMinutes, Is.EqualTo(60));
        Assert.That(merged.Metrics.BatchSize, Is.EqualTo(global.Metrics.BatchSize), "demais campos herdam");
        Assert.That(merged.Triage.EnqueueOnCreate, Is.False);
        Assert.That(merged.Triage.BatchSize, Is.EqualTo(global.Triage.BatchSize));
    }

    [Test]
    public void Merge_DoesNotAllowClientToChangeTheTick()
    {
        var global = new BackgroundProcessingSettings();
        global.Metrics.TickSeconds = 600;
        global.Triage.TickSeconds = 90;

        // O override não possui campos de tick — nada que o cliente envie os altera.
        var client = new BackgroundProcessingSettingsOverride
        {
            Metrics = new TechnicianMetricsProcessingOverride { BatchSize = 50 },
            Triage = new TicketTriageProcessingOverride { MaxPerClientPerRun = 3 }
        };

        var merged = BackgroundProcessingSettingsMerger.Merge(global, client);

        Assert.That(merged.Metrics.TickSeconds, Is.EqualTo(600));
        Assert.That(merged.Triage.TickSeconds, Is.EqualTo(90));
    }

    [Test]
    public void Normalize_ClampsFloorsAndCeilings()
    {
        var settings = new BackgroundProcessingSettings();
        settings.Metrics.TickSeconds = 1;
        settings.Metrics.IntervalMinutes = 1;
        settings.Metrics.BatchSize = 100000;
        settings.Triage.TickSeconds = 1;
        settings.Triage.IntervalSeconds = 1;
        settings.Triage.BatchSize = 100000;

        var normalized = BackgroundProcessingSettingsMerger.Normalize(settings);

        Assert.That(normalized.Metrics.TickSeconds, Is.EqualTo(TechnicianMetricsProcessingSettings.MinimumTickSeconds));
        Assert.That(normalized.Metrics.IntervalMinutes, Is.EqualTo(TechnicianMetricsProcessingSettings.MinimumIntervalMinutes),
            "piso de 10 min nas métricas");
        Assert.That(normalized.Metrics.BatchSize, Is.EqualTo(TechnicianMetricsProcessingSettings.MaximumBatchSize));
        Assert.That(normalized.Triage.TickSeconds, Is.EqualTo(TicketTriageProcessingSettings.MinimumTickSeconds));
        Assert.That(normalized.Triage.IntervalSeconds, Is.EqualTo(TicketTriageProcessingSettings.MinimumIntervalSeconds));
        Assert.That(normalized.Triage.BatchSize, Is.EqualTo(TicketTriageProcessingSettings.MaximumBatchSize));
    }
}
