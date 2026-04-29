namespace Discovery.Api.Services.BackgroundServices;

/// <summary>
/// Snapshot of a background service state. Returned by the dashboard endpoints.
/// </summary>
public sealed record BackgroundServiceSnapshot(
    string Name,
    string TypeName,
    string Category,
    BackgroundServiceState State,
    DateTime? StartedAtUtc,
    DateTime? LastHeartbeatUtc,
    TimeSpan? Uptime,
    long IterationsCompleted,
    long ErrorsCount,
    string? LastError,
    DateTime? LastErrorAtUtc,
    string? StatusMessage,
    IReadOnlyDictionary<string, object?> CustomMetrics);

public enum BackgroundServiceState
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    Idle = 3,
    Degraded = 4,
    Stopped = 5,
    Faulted = 6
}

/// <summary>
/// Categorization for grouping services on the dashboard.
/// </summary>
public static class BackgroundServiceCategories
{
    public const string Messaging = "Messaging";
    public const string Knowledge = "Knowledge";
    public const string Alerts = "Alerts";
    public const string Reports = "Reports";
    public const string Reconciliation = "Reconciliation";
    public const string RemoteDebug = "RemoteDebug";
    public const string Maintenance = "Maintenance";
    public const string Bootstrap = "Bootstrap";
    public const string Sync = "Sync";
    public const string Quartz = "Quartz";
    public const string Other = "Other";
}
