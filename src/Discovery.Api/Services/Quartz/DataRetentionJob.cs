using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Unified data retention job that purges old records across multiple tables.
/// Respects centralized RetentionSettings from ServerConfiguration.
/// 
/// Purges:
/// - Expired/revoked user sessions
/// - Expired API tokens (with grace period)
/// - Read notifications (unread are never purged)
/// - Old completed/failed agent commands
/// - Stale sync ping deliveries
/// - Old P2P telemetry
/// - Old automation execution reports
///
/// Schedule: daily at 3:30 AM (0 30 3 * * ?)
/// </summary>
[DisallowConcurrentExecution]
public sealed class DataRetentionJob : IJob
{
    public static readonly JobKey Key = new("data-retention", "maintenance");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<DataRetentionJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var serverRepo = scope.ServiceProvider.GetRequiredService<IServerConfigurationRepository>();
        var server = await serverRepo.GetOrCreateDefaultAsync();

        var settings = ParseRetentionSettings(server.RetentionSettingsJson);
        var now = DateTime.UtcNow;

        var results = new Dictionary<string, int>();

        // 1. Expired/revoked user sessions
        var sessionCutoff = now.AddDays(-settings.SessionRetentionDays);
        var sessionsDeleted = await db.UserSessions
            .Where(s => s.ExpiresAt < sessionCutoff || (s.RevokedAt != null && s.RevokedAt < sessionCutoff))
            .ExecuteDeleteAsync(ct);
        if (sessionsDeleted > 0)
            logger.LogInformation("DataRetention: deleted {Count} expired user sessions.", sessionsDeleted);
        results["sessions"] = sessionsDeleted;

        // 2. Expired API tokens (past expiry + grace period, or inactive for long)
        var tokenCutoff = now.AddDays(-(settings.TokenExpiredGraceDays));
        var tokensDeleted = await db.ApiTokens
            .Where(t => (t.ExpiresAt != null && t.ExpiresAt < tokenCutoff) || (!t.IsActive && t.CreatedAt < now.AddDays(-90)))
            .ExecuteDeleteAsync(ct);
        if (tokensDeleted > 0)
            logger.LogInformation("DataRetention: deleted {Count} expired/revoked API tokens.", tokensDeleted);
        results["apiTokens"] = tokensDeleted;

        // 3. Read notifications
        var notifCutoff = now.AddDays(-settings.NotificationRetentionDays);
        var notifsDeleted = await db.AppNotifications
            .Where(n => n.IsRead && n.CreatedAt < notifCutoff)
            .ExecuteDeleteAsync(ct);
        if (notifsDeleted > 0)
            logger.LogInformation("DataRetention: deleted {Count} read notifications.", notifsDeleted);
        results["notifications"] = notifsDeleted;

        // 4. Completed/failed agent commands
        var cmdCutoff = now.AddDays(-settings.AgentCommandRetentionDays);
        var completedStatuses = new[] { Discovery.Core.Enums.CommandStatus.Completed, Discovery.Core.Enums.CommandStatus.Failed, Discovery.Core.Enums.CommandStatus.Cancelled, Discovery.Core.Enums.CommandStatus.Timeout };
        var cmdsDeleted = await db.AgentCommands
            .Where(c => c.CompletedAt.HasValue && c.CompletedAt < cmdCutoff && completedStatuses.Contains(c.Status))
            .ExecuteDeleteAsync(ct);
        if (cmdsDeleted > 0)
            logger.LogInformation("DataRetention: deleted {Count} old agent commands.", cmdsDeleted);
        results["agentCommands"] = cmdsDeleted;

        // 5. Sync ping deliveries
        var pingCutoff = now.AddDays(-settings.SyncPingRetentionDays);
        var pingsDeleted = await db.SyncPingDeliveries
            .Where(p => p.CreatedAt < pingCutoff)
            .ExecuteDeleteAsync(ct);
        if (pingsDeleted > 0)
            logger.LogInformation("DataRetention: deleted {Count} sync ping deliveries.", pingsDeleted);
        results["syncPings"] = pingsDeleted;

        // 6. P2P agent telemetry
        var telemetryCutoff = now.AddDays(-settings.TelemetryRetentionDays);
        var telemetryDeleted = await db.P2pAgentTelemetries
            .Where(t => t.ReceivedAt < telemetryCutoff)
            .ExecuteDeleteAsync(ct);
        if (telemetryDeleted > 0)
            logger.LogInformation("DataRetention: deleted {Count} P2P telemetry records.", telemetryDeleted);
        results["p2pTelemetry"] = telemetryDeleted;

        // 7. Automation execution reports
        var autoReportCutoff = now.AddDays(-settings.AutomationReportRetentionDays);
        var autoReportsDeleted = await db.AutomationExecutionReports
            .Where(r => r.StartedAt.HasValue && r.StartedAt < autoReportCutoff)
            .ExecuteDeleteAsync(ct);
        if (autoReportsDeleted > 0)
            logger.LogInformation("DataRetention: deleted {Count} automation execution reports.", autoReportsDeleted);
        results["automationReports"] = autoReportsDeleted;

        var totalDeleted = results.Values.Sum();
        logger.LogInformation("DataRetention completed. Total purged: {Total} records across {Tables} tables.",
            totalDeleted, results.Count(r => r.Value > 0));

        context.Result = results;
    }

    private static RetentionSettings ParseRetentionSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new RetentionSettings();

        try
        {
            return JsonSerializer.Deserialize<RetentionSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new RetentionSettings();
        }
        catch
        {
            return new RetentionSettings();
        }
    }
}
