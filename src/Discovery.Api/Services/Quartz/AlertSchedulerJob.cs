using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que monitora alertas agendados e os despacha quando o momento chegou.
/// Substitui AlertSchedulerBackgroundService.
/// Schedule: a cada 30s.
/// </summary>
[DisallowConcurrentExecution]
public sealed class AlertSchedulerJob : IJob
{
    public static readonly JobKey Key = new("alert-scheduler", "alerts");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<AlertSchedulerJob>();
        var ct = context.CancellationToken;

        try
        {
            await ProcessPendingAlertsAsync(scopeFactory, logger, ct);
            await ExpireStaleAlertsAsync(scopeFactory, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AlertScheduler: erro no ciclo de processamento.");
        }
    }

    private static async Task ProcessPendingAlertsAsync(
        IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var alertRepo = scope.ServiceProvider.GetRequiredService<IAgentAlertRepository>();
        var dispatchService = scope.ServiceProvider.GetRequiredService<AlertDispatchService>();

        var pending = await alertRepo.GetPendingScheduledAsync(DateTime.UtcNow);
        if (pending.Count > 0)
            logger.LogInformation("AlertScheduler: {Count} alerta(s) agendado(s) prontos para despacho.", pending.Count);

        foreach (var alert in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await dispatchService.DispatchAsync(alert, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AlertScheduler: erro ao despachar alerta {AlertId}.", alert.Id);
            }
        }
    }

    private static async Task ExpireStaleAlertsAsync(
        IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken ct)
    {
        _ = ct;
        await using var scope = scopeFactory.CreateAsyncScope();
        var alertRepo = scope.ServiceProvider.GetRequiredService<IAgentAlertRepository>();

        var expired = await alertRepo.GetExpiredAsync(DateTime.UtcNow);
        foreach (var alert in expired)
        {
            await alertRepo.UpdateStatusAsync(alert.Id, AlertDefinitionStatus.Expired);
            logger.LogInformation("AlertScheduler: alerta {AlertId} marcado como Expired.", alert.Id);
        }
    }
}
