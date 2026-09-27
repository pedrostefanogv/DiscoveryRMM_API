namespace Discovery.Core.Configuration;

/// <summary>
/// Mescla o override de cliente sobre a configuração global e normaliza faixas.
/// Funções puras (testáveis sem banco) usadas pelo ConfigurationResolver.
/// </summary>
public static class BackgroundProcessingSettingsMerger
{
    public static BackgroundProcessingSettings Merge(
        BackgroundProcessingSettings global, BackgroundProcessingSettingsOverride? client)
    {
        var result = new BackgroundProcessingSettings
        {
            Metrics = Clone(global.Metrics),
            Triage = Clone(global.Triage)
        };

        if (client?.Metrics is { } metricsOverride)
            ApplyMetricsOverride(result.Metrics, metricsOverride);

        if (client?.Triage is { } triageOverride)
            ApplyTriageOverride(result.Triage, triageOverride);

        return Normalize(result);
    }

    /// <summary>Aplica apenas os campos não-nulos (null = herdar).</summary>
    public static void ApplyMetricsOverride(
        TechnicianMetricsProcessingSettings target, TechnicianMetricsProcessingOverride ov)
    {
        if (ov.Enabled.HasValue) target.Enabled = ov.Enabled.Value;
        if (ov.IntervalMinutes.HasValue) target.IntervalMinutes = ov.IntervalMinutes.Value;
        if (ov.StaleThresholdMinutes.HasValue) target.StaleThresholdMinutes = ov.StaleThresholdMinutes.Value;
        if (ov.WindowDays.HasValue) target.WindowDays = ov.WindowDays.Value;
        if (ov.BatchSize.HasValue) target.BatchSize = ov.BatchSize.Value;
        if (ov.MaxBatchesPerRun.HasValue) target.MaxBatchesPerRun = ov.MaxBatchesPerRun.Value;
        if (ov.MaxRunSeconds.HasValue) target.MaxRunSeconds = ov.MaxRunSeconds.Value;
        if (ov.BootstrapMissingSnapshots.HasValue) target.BootstrapMissingSnapshots = ov.BootstrapMissingSnapshots.Value;
    }

    public static void ApplyTriageOverride(
        TicketTriageProcessingSettings target, TicketTriageProcessingOverride ov)
    {
        if (ov.Enabled.HasValue) target.Enabled = ov.Enabled.Value;
        if (ov.EnqueueOnCreate.HasValue) target.EnqueueOnCreate = ov.EnqueueOnCreate.Value;
        if (ov.IntervalSeconds.HasValue) target.IntervalSeconds = ov.IntervalSeconds.Value;
        if (ov.BatchSize.HasValue) target.BatchSize = ov.BatchSize.Value;
        if (ov.MaxPerClientPerRun.HasValue) target.MaxPerClientPerRun = ov.MaxPerClientPerRun.Value;
        if (ov.MaxAttempts.HasValue) target.MaxAttempts = ov.MaxAttempts.Value;
        if (ov.RetryAfterMinutes.HasValue) target.RetryAfterMinutes = ov.RetryAfterMinutes.Value;
        if (ov.BatchDelaySeconds.HasValue) target.BatchDelaySeconds = ov.BatchDelaySeconds.Value;
    }

    /// <summary>
    /// Normaliza faixas (piso do intervalo de métricas = 10 min por decisão de
    /// produto). Valores fora da faixa são ajustados em vez de descartados — o
    /// ciclo nunca para por configuração inválida.
    /// </summary>
    public static BackgroundProcessingSettings Normalize(BackgroundProcessingSettings settings)
    {
        var metrics = settings.Metrics;
        metrics.TickSeconds = Math.Clamp(metrics.TickSeconds,
            TechnicianMetricsProcessingSettings.MinimumTickSeconds,
            TechnicianMetricsProcessingSettings.MaximumTickSeconds);
        metrics.IntervalMinutes = Math.Clamp(metrics.IntervalMinutes,
            TechnicianMetricsProcessingSettings.MinimumIntervalMinutes,
            TechnicianMetricsProcessingSettings.MaximumIntervalMinutes);
        metrics.StaleThresholdMinutes = Math.Clamp(metrics.StaleThresholdMinutes,
            TechnicianMetricsProcessingSettings.MinimumStaleThresholdMinutes,
            TechnicianMetricsProcessingSettings.MaximumIntervalMinutes);
        metrics.WindowDays = Math.Clamp(metrics.WindowDays,
            TechnicianMetricsProcessingSettings.MinimumWindowDays,
            TechnicianMetricsProcessingSettings.MaximumWindowDays);
        metrics.BatchSize = Math.Clamp(metrics.BatchSize,
            TechnicianMetricsProcessingSettings.MinimumBatchSize,
            TechnicianMetricsProcessingSettings.MaximumBatchSize);
        metrics.MaxBatchesPerRun = Math.Clamp(metrics.MaxBatchesPerRun, 1,
            TechnicianMetricsProcessingSettings.MaximumBatchesPerRun);
        metrics.MaxRunSeconds = Math.Clamp(metrics.MaxRunSeconds,
            TechnicianMetricsProcessingSettings.MinimumRunSeconds,
            TechnicianMetricsProcessingSettings.MaximumRunSeconds);

        var triage = settings.Triage;
        triage.TickSeconds = Math.Clamp(triage.TickSeconds,
            TicketTriageProcessingSettings.MinimumTickSeconds,
            TicketTriageProcessingSettings.MaximumTickSeconds);
        triage.IntervalSeconds = Math.Clamp(triage.IntervalSeconds,
            TicketTriageProcessingSettings.MinimumIntervalSeconds,
            TicketTriageProcessingSettings.MaximumIntervalSeconds);
        triage.BatchSize = Math.Clamp(triage.BatchSize,
            TicketTriageProcessingSettings.MinimumBatchSize,
            TicketTriageProcessingSettings.MaximumBatchSize);
        triage.MaxPerClientPerRun = Math.Clamp(triage.MaxPerClientPerRun, 1,
            TicketTriageProcessingSettings.MaximumPerClientPerRun);
        triage.MaxAttempts = Math.Clamp(triage.MaxAttempts, 1,
            TicketTriageProcessingSettings.MaximumAttempts);
        triage.RetryAfterMinutes = Math.Clamp(triage.RetryAfterMinutes, 1,
            TicketTriageProcessingSettings.MaximumRetryAfterMinutes);
        triage.BatchDelaySeconds = Math.Clamp(triage.BatchDelaySeconds, 0,
            TicketTriageProcessingSettings.MaximumIntervalSeconds);

        return settings;
    }

    private static TechnicianMetricsProcessingSettings Clone(TechnicianMetricsProcessingSettings source) => new()
    {
        Enabled = source.Enabled,
        TickSeconds = source.TickSeconds,
        IntervalMinutes = source.IntervalMinutes,
        StaleThresholdMinutes = source.StaleThresholdMinutes,
        WindowDays = source.WindowDays,
        BatchSize = source.BatchSize,
        MaxBatchesPerRun = source.MaxBatchesPerRun,
        MaxRunSeconds = source.MaxRunSeconds,
        BootstrapMissingSnapshots = source.BootstrapMissingSnapshots
    };

    private static TicketTriageProcessingSettings Clone(TicketTriageProcessingSettings source) => new()
    {
        Enabled = source.Enabled,
        EnqueueOnCreate = source.EnqueueOnCreate,
        TickSeconds = source.TickSeconds,
        IntervalSeconds = source.IntervalSeconds,
        BatchSize = source.BatchSize,
        MaxPerClientPerRun = source.MaxPerClientPerRun,
        MaxAttempts = source.MaxAttempts,
        RetryAfterMinutes = source.RetryAfterMinutes,
        BatchDelaySeconds = source.BatchDelaySeconds
    };
}
