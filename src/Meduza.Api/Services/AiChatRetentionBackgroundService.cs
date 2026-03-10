using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LogLevelEnum = Meduza.Core.Enums.LogLevel;

namespace Meduza.Api.Services;

/// <summary>
/// Background service que executa a retenção de conversas de AI Chat
/// - Soft delete de sessões expiradas (> 180 dias)
/// - Hard delete de sessões soft-deleted há mais de 30 dias (LGPD compliance)
/// </summary>
public class AiChatRetentionBackgroundService : BackgroundService
{
    private const int RetentionDays = 180;
    private const int GracePeriodDays = 30;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiChatRetentionBackgroundService> _logger;

    public AiChatRetentionBackgroundService(
        IServiceScopeFactory scopeFactory, 
        ILogger<AiChatRetentionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Executa primeira vez após 1 hora de startup
        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        await RunRetentionAsync(stoppingToken);

        // Depois executa a cada 24 horas
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunRetentionAsync(stoppingToken);
        }
    }

    private async Task RunRetentionAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting AI chat retention job");

            using var scope = _scopeFactory.CreateScope();
            var sessionRepo = scope.ServiceProvider.GetRequiredService<IAiChatSessionRepository>();
            var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();

            var now = DateTime.UtcNow;
            var expiryCutoff = now.AddDays(-RetentionDays);
            var deleteCutoff = now.AddDays(-(RetentionDays + GracePeriodDays));

            // 1. Soft delete sessões expiradas
            var expiredSessions = await sessionRepo.GetExpiredAsync(expiryCutoff, 1000, stoppingToken);
            var softDeleteCount = 0;
            foreach (var session in expiredSessions)
            {
                await sessionRepo.SoftDeleteAsync(session.Id, stoppingToken);
                softDeleteCount++;
            }

            // 2. Hard delete sessões soft-deleted há mais de 30 dias (LGPD)
            var hardDeleteCount = await sessionRepo.HardDeleteAsync(deleteCutoff, stoppingToken);

            _logger.LogInformation(
                "AI chat retention completed: soft-deleted {SoftCount}, hard-deleted {HardCount}",
                softDeleteCount, hardDeleteCount);

            // 3. Log de auditoria
            await loggingService.LogAsync(
                LogLevelEnum.Info,
                LogType.System,
                LogSource.Scheduler,
                $"AI chat retention: {softDeleteCount} expired, {hardDeleteCount} purged",
                dataJson: new
                {
                    softDeleteCount,
                    hardDeleteCount,
                    retentionDays = RetentionDays,
                    gracePeriodDays = GracePeriodDays,
                    expiryCutoff,
                    deleteCutoff
                },
                cancellationToken: stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "AI chat retention job failed");
        }
    }
}
