using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job that purges old log entries based on configured retention policy.
/// Replaces LogPurgeBackgroundService.
/// Schedule: daily at 3 AM (0 0 3 * * ?)
/// </summary>
[DisallowConcurrentExecution]
public sealed class LogPurgeJob : IJob
{
    public static readonly JobKey Key = new("log-purge", "maintenance");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<LogPurgeJob>();

        var retentionDays = GetRetentionDays(config);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        await using var scope = scopeFactory.CreateAsyncScope();
        var logRepo = scope.ServiceProvider.GetRequiredService<ILogRepository>();
        var deleted = await logRepo.PurgeAsync(cutoff);

        logger.LogInformation("Log purge completed. Deleted {Count} entries older than {Days} days.", deleted, retentionDays);
        context.Result = deleted;
    }

    private static int GetRetentionDays(IConfiguration config)
    {
        var allowed = config.GetSection("LogRetention:AllowedDays").Get<int[]>() ?? [30, 90, 180, 365];
        var configured = config.GetValue<int?>("LogRetention:Days") ?? 90;
        return allowed.Contains(configured) ? configured : 90;
    }
}
