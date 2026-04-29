using System.Collections.Concurrent;

namespace Discovery.Api.Services.BackgroundServices;

/// <summary>
/// Central, thread-safe registry holding observability state for every
/// <see cref="IHostedService"/> in the application. Services may opt-in to
/// rich reporting by injecting the registry and calling <see cref="Report"/> /
/// <see cref="Heartbeat"/>; if a service never reports, the registry still
/// surfaces it (state = Unknown) thanks to startup auto-discovery.
///
/// Lifetime: Singleton.
/// </summary>
public sealed class BackgroundServiceRegistry
{
    private readonly ConcurrentDictionary<string, BackgroundServiceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a service (called once during startup, or lazily on first report).
    /// Idempotent.
    /// </summary>
    public void EnsureRegistered(string name, string typeName, string category)
    {
        _entries.GetOrAdd(name, n => new BackgroundServiceEntry
        {
            Name = n,
            TypeName = typeName,
            Category = category,
            State = BackgroundServiceState.Unknown
        });
    }

    /// <summary>
    /// Marks the service as Started and stamps the start time. Call from
    /// <c>ExecuteAsync</c> / <c>StartAsync</c>.
    /// </summary>
    public void MarkStarted(string name)
    {
        var entry = GetOrCreate(name);
        lock (entry.Sync)
        {
            entry.StartedAtUtc ??= DateTime.UtcNow;
            entry.State = BackgroundServiceState.Running;
            entry.LastHeartbeatUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Marks the service as Stopped.
    /// </summary>
    public void MarkStopped(string name)
    {
        var entry = GetOrCreate(name);
        lock (entry.Sync)
        {
            entry.State = BackgroundServiceState.Stopped;
            entry.LastHeartbeatUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records a heartbeat (called once per loop iteration).
    /// </summary>
    public void Heartbeat(string name, string? statusMessage = null)
    {
        var entry = GetOrCreate(name);
        lock (entry.Sync)
        {
            entry.LastHeartbeatUtc = DateTime.UtcNow;
            entry.IterationsCompleted++;
            if (entry.State == BackgroundServiceState.Unknown || entry.State == BackgroundServiceState.Idle)
                entry.State = BackgroundServiceState.Running;
            if (!string.IsNullOrWhiteSpace(statusMessage))
                entry.StatusMessage = statusMessage;
        }
    }

    /// <summary>
    /// Updates the state and optionally a status message.
    /// </summary>
    public void SetState(string name, BackgroundServiceState state, string? statusMessage = null)
    {
        var entry = GetOrCreate(name);
        lock (entry.Sync)
        {
            entry.State = state;
            entry.LastHeartbeatUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(statusMessage))
                entry.StatusMessage = statusMessage;
        }
    }

    /// <summary>
    /// Records an error (increments counter, captures message).
    /// </summary>
    public void ReportError(string name, Exception ex)
    {
        var entry = GetOrCreate(name);
        lock (entry.Sync)
        {
            entry.ErrorsCount++;
            entry.LastError = ex.Message;
            entry.LastErrorAtUtc = DateTime.UtcNow;
            entry.LastHeartbeatUtc = DateTime.UtcNow;
            // Don't override Stopped; otherwise mark Degraded.
            if (entry.State != BackgroundServiceState.Stopped)
                entry.State = BackgroundServiceState.Degraded;
        }
    }

    /// <summary>
    /// Records a custom metric (key/value). Common keys: <c>queueDepth</c>,
    /// <c>processed</c>, <c>lastDispatchUtc</c>.
    /// </summary>
    public void SetMetric(string name, string key, object? value)
    {
        var entry = GetOrCreate(name);
        lock (entry.Sync)
        {
            entry.CustomMetrics[key] = value;
        }
    }

    /// <summary>
    /// Returns a snapshot of every registered service.
    /// </summary>
    public IReadOnlyList<BackgroundServiceSnapshot> Snapshot()
    {
        return _entries.Values.Select(ToSnapshot).OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Returns a snapshot of a single service, or <c>null</c> if not registered.
    /// </summary>
    public BackgroundServiceSnapshot? Get(string name)
    {
        return _entries.TryGetValue(name, out var entry) ? ToSnapshot(entry) : null;
    }

    private BackgroundServiceEntry GetOrCreate(string name)
    {
        return _entries.GetOrAdd(name, n => new BackgroundServiceEntry
        {
            Name = n,
            TypeName = n,
            Category = BackgroundServiceCategories.Other,
            State = BackgroundServiceState.Unknown
        });
    }

    private static BackgroundServiceSnapshot ToSnapshot(BackgroundServiceEntry entry)
    {
        lock (entry.Sync)
        {
            var uptime = entry.StartedAtUtc.HasValue
                ? DateTime.UtcNow - entry.StartedAtUtc.Value
                : (TimeSpan?)null;

            return new BackgroundServiceSnapshot(
                Name: entry.Name,
                TypeName: entry.TypeName,
                Category: entry.Category,
                State: entry.State,
                StartedAtUtc: entry.StartedAtUtc,
                LastHeartbeatUtc: entry.LastHeartbeatUtc,
                Uptime: uptime,
                IterationsCompleted: entry.IterationsCompleted,
                ErrorsCount: entry.ErrorsCount,
                LastError: entry.LastError,
                LastErrorAtUtc: entry.LastErrorAtUtc,
                StatusMessage: entry.StatusMessage,
                CustomMetrics: new Dictionary<string, object?>(entry.CustomMetrics));
        }
    }

    private sealed class BackgroundServiceEntry
    {
        public required string Name { get; init; }
        public required string TypeName { get; set; }
        public required string Category { get; set; }
        public BackgroundServiceState State { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? LastHeartbeatUtc { get; set; }
        public long IterationsCompleted { get; set; }
        public long ErrorsCount { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastErrorAtUtc { get; set; }
        public string? StatusMessage { get; set; }
        public Dictionary<string, object?> CustomMetrics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object Sync { get; } = new();
    }
}

/// <summary>
/// Maps known background service type names to their dashboard category.
/// Used by startup auto-discovery so the registry shows every service even
/// before any reports a heartbeat.
/// </summary>
public static class BackgroundServiceCategoryMap
{
    private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Messaging
        ["NatsBackgroundService"] = BackgroundServiceCategories.Messaging,
        ["NatsSignalRBridge"] = BackgroundServiceCategories.Messaging,
        ["NatsAuthCalloutBackgroundService"] = BackgroundServiceCategories.Messaging,
        // Knowledge
        ["KnowledgeEmbeddingBackgroundService"] = BackgroundServiceCategories.Knowledge,
        ["KnowledgeEmbeddingQueueBackgroundService"] = BackgroundServiceCategories.Knowledge,
        // Alerts
        ["AlertSchedulerBackgroundService"] = BackgroundServiceCategories.Alerts,
        ["SlaMonitoringBackgroundService"] = BackgroundServiceCategories.Alerts,
        // Reports
        ["ReportGenerationBackgroundService"] = BackgroundServiceCategories.Reports,
        // Reconciliation
        ["AgentLabelingReconciliationBackgroundService"] = BackgroundServiceCategories.Reconciliation,
        ["MeshCentralIdentityReconciliationBackgroundService"] = BackgroundServiceCategories.Reconciliation,
        ["MeshCentralGroupPolicyReconciliationBackgroundService"] = BackgroundServiceCategories.Reconciliation,
        // Remote debug
        ["RemoteDebugNatsBridgeService"] = BackgroundServiceCategories.RemoteDebug,
        ["RemoteDebugSessionCleanupService"] = BackgroundServiceCategories.RemoteDebug,
        // Sync
        ["SyncPingDispatchBackgroundService"] = BackgroundServiceCategories.Sync,
        ["AiChatJobBackgroundService"] = BackgroundServiceCategories.Sync,
        // Bootstrap
        ["AgentPackagePrebuildHostedService"] = BackgroundServiceCategories.Bootstrap,
    };

    public static string Resolve(string typeName)
        => _map.TryGetValue(typeName, out var category) ? category : BackgroundServiceCategories.Other;
}
