using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Recalcula periodicamente os snapshots de métricas por atendente usados pela
/// triagem por IA. A leitura sob demanda também recalcula com TTL de 15 min, o
/// que mantém este job como otimização (e não dependência).
///
/// Configuração (appsettings):
/// - BackgroundJobs:TechnicianMetrics:Enabled (default: true)
/// - BackgroundJobs:TechnicianMetrics:IntervalMinutes (default: 15, piso 5)
/// - BackgroundJobs:TechnicianMetrics:StartupDelaySeconds (default: 60)
/// </summary>
[DisallowConcurrentExecution]
public sealed class TechnicianMetricsRefreshJob : IJob
{
    public static readonly JobKey Key = new("technician-metrics-refresh", "tickets");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<TechnicianMetricsRefreshJob>();
        var ct = context.CancellationToken;

        if (!(config.GetValue<bool?>("BackgroundJobs:TechnicianMetrics:Enabled") ?? true))
        {
            logger.LogDebug("Refresh de métricas de atendentes desabilitado por configuração.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var metrics = scope.ServiceProvider.GetRequiredService<ITechnicianMetricsService>();

        try
        {
            var saved = await metrics.RefreshSnapshotsAsync(null, null, ct);
            context.Result = saved;

            if (saved > 0)
                logger.LogInformation("Métricas de atendentes recalculadas: {Count} snapshots.", saved);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao recalcular as métricas de atendentes.");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
