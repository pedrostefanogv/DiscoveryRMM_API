using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que processa execuções pendentes de relatórios.
/// Substitui ReportGenerationBackgroundService.
/// Schedule: a cada 15 segundos.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReportGenerationJob : IJob
{
    public static readonly JobKey Key = new("report-generation", "reports");
    private const int BatchSize = 10;

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<ReportGenerationJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var reportService = scope.ServiceProvider.GetRequiredService<IReportService>();

        try
        {
            var processed = await reportService.ProcessPendingAsync(BatchSize, ct);
            if (processed.Count > 0)
            {
                logger.LogInformation("Processed {Count} pending report executions.", processed.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error while processing pending report executions.");
        }
    }
}
