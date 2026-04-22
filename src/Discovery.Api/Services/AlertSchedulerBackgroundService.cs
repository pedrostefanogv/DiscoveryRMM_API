using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Background service que monitora alertas agendados e os despacha quando o momento chegou.
/// Loop de 30 segundos. Pode ser habilitado/desabilitado via BackgroundJobs:AlertSchedulerEnabled.
/// </summary>
public class AlertSchedulerBackgroundService : BackgroundService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlertSchedulerBackgroundService> _logger;

    public AlertSchedulerBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<AlertSchedulerBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AlertSchedulerBackgroundService iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAlertsAsync(stoppingToken);
                await ExpireStaleAlertsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AlertScheduler: erro no ciclo de processamento.");
            }

            await Task.Delay(LoopInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingAlertsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var alertRepo = scope.ServiceProvider.GetRequiredService<IAgentAlertRepository>();
        var dispatchService = scope.ServiceProvider.GetRequiredService<AlertDispatchService>();

        var pending = await alertRepo.GetPendingScheduledAsync(DateTime.UtcNow);

        if (pending.Count > 0)
            _logger.LogInformation("AlertScheduler: {Count} alerta(s) agendado(s) prontos para despacho.", pending.Count);

        foreach (var alert in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await dispatchService.DispatchAsync(alert, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AlertScheduler: erro ao despachar alerta {AlertId}.", alert.Id);
            }
        }
    }

    private async Task ExpireStaleAlertsAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var alertRepo = scope.ServiceProvider.GetRequiredService<IAgentAlertRepository>();

        var expired = await alertRepo.GetExpiredAsync(DateTime.UtcNow);

        foreach (var alert in expired)
        {
            await alertRepo.UpdateStatusAsync(alert.Id, AlertDefinitionStatus.Expired);
            _logger.LogInformation("AlertScheduler: alerta {AlertId} marcado como Expired.", alert.Id);
        }
    }
}
