using Discovery.Core.Interfaces;
using Discovery.Core.Enums;
using LogLevelEnum = Discovery.Core.Enums.LogLevel;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job that enforces AI chat retention policies:
/// - Soft delete expired sessions (> 180 days)
/// - Hard delete soft-deleted sessions (> 210 days — LGPD compliance)
/// Replaces AiChatRetentionBackgroundService.
/// Schedule: daily at 2 AM (0 0 2 * * ?)
/// </summary>
[DisallowConcurrentExecution]
public sealed class AiChatRetentionJob : IJob
{
    public static readonly JobKey Key = new("ai-chat-retention", "maintenance");
    private const int RetentionDays = 180;
    private const int GracePeriodDays = 30;

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<AiChatRetentionJob>();

        using var scope = scopeFactory.CreateScope();
        var sessionRepo = scope.ServiceProvider.GetRequiredService<IAiChatSessionRepository>();
        var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
        var ct = context.CancellationToken;

        var now = DateTime.UtcNow;
        var expiryCutoff = now.AddDays(-RetentionDays);
        var deleteCutoff = now.AddDays(-(RetentionDays + GracePeriodDays));

        var expiredSessions = await sessionRepo.GetExpiredAsync(expiryCutoff, 1000, ct);
        var softDeleteCount = 0;
        foreach (var session in expiredSessions)
        {
            await sessionRepo.SoftDeleteAsync(session.Id, ct);
            softDeleteCount++;
        }

        var hardDeleteCount = await sessionRepo.HardDeleteAsync(deleteCutoff, ct);

        logger.LogInformation("AI chat retention: soft-deleted {SoftCount}, hard-deleted {HardCount}", softDeleteCount, hardDeleteCount);

        await loggingService.LogAsync(LogLevelEnum.Info, LogType.System, LogSource.Scheduler,
            $"AI chat retention: {softDeleteCount} expired, {hardDeleteCount} purged",
            dataJson: new { softDeleteCount, hardDeleteCount, retentionDays = RetentionDays, gracePeriodDays = GracePeriodDays, expiryCutoff, deleteCutoff },
            cancellationToken: ct);

        context.Result = new { softDeleteCount, hardDeleteCount };
    }
}
