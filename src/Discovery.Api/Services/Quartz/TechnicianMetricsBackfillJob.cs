using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Executa o backfill de snapshots de métricas em lotes. Cada ciclo processa um
/// lote do pedido mais antigo pendente/em andamento (progresso persistido em
/// processing_scope_state). Inerte quando não há pedido.
///
/// Configuração (appsettings):
/// - BackgroundJobs:TechnicianMetricsBackfill:Enabled (default: true)
/// - BackgroundJobs:TechnicianMetricsBackfill:IntervalSeconds (default: 30, piso 10)
/// </summary>
[DisallowConcurrentExecution]
public sealed class TechnicianMetricsBackfillJob : IJob
{
    public static readonly JobKey Key = new("technician-metrics-backfill", "tickets");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<TechnicianMetricsBackfillJob>();
        var ct = context.CancellationToken;

        if (!(config.GetValue<bool?>("BackgroundJobs:TechnicianMetricsBackfill:Enabled") ?? true))
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var backfill = scope.ServiceProvider.GetRequiredService<IBackgroundProcessingBackfillService>();

        try
        {
            var state = await backfill.ProcessNextBatchAsync(ct);
            context.Result = state;

            if (state is not null)
            {
                logger.LogInformation(
                    "Backfill de métricas ({Scope}): {Processed}/{Total} — {Status}.",
                    state.ClientId, state.Processed, state.Total, state.Status);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha no backfill de snapshots de métricas.");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
