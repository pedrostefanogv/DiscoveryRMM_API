using System.Text.Json.Serialization;

namespace Discovery.Core.Configuration;

/// <summary>
/// Centralized retention and database maintenance settings.
/// Persisted in ServerConfiguration.RetentionSettingsJson.
/// </summary>
public class RetentionSettings
{
    // ── Data retention (days) ──

    /// <summary>Days to keep log entries. Default: 90.</summary>
    public int LogRetentionDays { get; set; } = 90;

    /// <summary>Days to keep read notifications. Unread notifications are never purged. Default: 60.</summary>
    public int NotificationRetentionDays { get; set; } = 60;

    /// <summary>Days to keep completed/failed agent commands. Default: 30.</summary>
    public int AgentCommandRetentionDays { get; set; } = 30;

    /// <summary>Days to keep expired/revoked user sessions. Default: 30.</summary>
    public int SessionRetentionDays { get; set; } = 30;

    /// <summary>Grace period (days) after token expiry before hard delete. Default: 7.</summary>
    public int TokenExpiredGraceDays { get; set; } = 7;

    /// <summary>Days to keep sync ping delivery records. Default: 7.</summary>
    public int SyncPingRetentionDays { get; set; } = 7;

    /// <summary>Days to keep P2P agent telemetry. Default: 7.</summary>
    public int TelemetryRetentionDays { get; set; } = 7;

    /// <summary>Days to keep automation execution reports. Default: 30.</summary>
    public int AutomationReportRetentionDays { get; set; } = 30;

    // ── AI Chat (kept in sync with AiChatRetentionJob) ──

    /// <summary>Days before soft-deleting AI chat sessions. Default: 180.</summary>
    public int AiChatExpiryDays { get; set; } = 180;

    /// <summary>Grace period after soft delete before hard delete. Default: 30.</summary>
    public int AiChatGraceDays { get; set; } = 30;

    // ── Database Maintenance ──

    public DatabaseMaintenanceSettings? DatabaseMaintenance { get; set; }
}

public class DatabaseMaintenanceSettings
{
    /// <summary>Enable scheduled database maintenance. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Quartz cron expression for maintenance window.
    /// Default: "0 0 3 ? * SUN" (Sunday 3 AM).
    /// </summary>
    public string Schedule { get; set; } = "0 0 3 ? * SUN";

    /// <summary>Run VACUUM FULL on ALL user tables (reclaims disk space, heavy).</summary>
    public bool VacuumFull { get; set; } = true;

    /// <summary>Run REINDEX on ALL user table indexes (rebuilds fragmented indexes).</summary>
    public bool Reindex { get; set; } = true;

    /// <summary>Run ANALYZE on ALL user tables (updates query planner statistics).</summary>
    public bool Analyze { get; set; } = true;

    /// <summary>Run VACUUM ANALYZE on ALL user tables (lighter, non-blocking). Runs after VACUUM FULL.</summary>
    public bool VacuumAnalyze { get; set; } = false;
}
