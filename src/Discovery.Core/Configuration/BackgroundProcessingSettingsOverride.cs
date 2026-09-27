namespace Discovery.Core.Configuration;

/// <summary>
/// Campos de BackgroundProcessingSettings que podem ser sobrescritos por cliente.
/// Semântica: null = herdar do global (mesmo padrão de AIIntegrationSettingsOverride).
/// </summary>
public sealed class BackgroundProcessingSettingsOverride
{
    public TechnicianMetricsProcessingOverride? Metrics { get; set; }

    public TicketTriageProcessingOverride? Triage { get; set; }
}

public sealed class TechnicianMetricsProcessingOverride
{
    public bool? Enabled { get; set; }
    public int? IntervalMinutes { get; set; }
    public int? StaleThresholdMinutes { get; set; }
    public int? WindowDays { get; set; }
    public int? BatchSize { get; set; }
    public int? MaxBatchesPerRun { get; set; }
    public int? MaxRunSeconds { get; set; }
    public bool? BootstrapMissingSnapshots { get; set; }
}

public sealed class TicketTriageProcessingOverride
{
    public bool? Enabled { get; set; }
    public bool? EnqueueOnCreate { get; set; }
    public int? IntervalSeconds { get; set; }
    public int? BatchSize { get; set; }
    public int? MaxPerClientPerRun { get; set; }
    public int? MaxAttempts { get; set; }
    public int? RetryAfterMinutes { get; set; }
    public int? BatchDelaySeconds { get; set; }
}
