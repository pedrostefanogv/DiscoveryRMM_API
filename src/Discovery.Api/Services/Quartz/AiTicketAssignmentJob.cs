using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Processa a fila da triagem por IA da auto-atribuição de chamados e aplica a
/// rede de segurança (chamados que continuam sem responsável após o tempo limite
/// recebem o fallback determinístico do departamento).
///
/// Configuração (appsettings):
/// - BackgroundJobs:AiTicketAssignment:Enabled (default: true)
/// - BackgroundJobs:AiTicketAssignment:IntervalSeconds (default: 20, piso 10)
/// - BackgroundJobs:AiTicketAssignment:StartupDelaySeconds (default: 20)
/// - BackgroundJobs:AiTicketAssignment:BatchSize (default: 25)
/// - BackgroundJobs:AiTicketAssignment:RetryAfterMinutes (default: 5)
/// </summary>
[DisallowConcurrentExecution]
public sealed class AiTicketAssignmentJob : IJob
{
    public static readonly JobKey Key = new("ai-ticket-assignment", "tickets");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<AiTicketAssignmentJob>();
        var ct = context.CancellationToken;

        if (!(config.GetValue<bool?>("BackgroundJobs:AiTicketAssignment:Enabled") ?? true))
        {
            logger.LogDebug("Triagem por IA desabilitada por configuração.");
            return;
        }

        var batchSize = Math.Clamp(config.GetValue<int?>("BackgroundJobs:AiTicketAssignment:BatchSize") ?? 25, 1, 200);
        var retryAfterMinutes = Math.Max(1, config.GetValue<int?>("BackgroundJobs:AiTicketAssignment:RetryAfterMinutes") ?? 5);

        await using var scope = scopeFactory.CreateAsyncScope();
        var triage = scope.ServiceProvider.GetRequiredService<IAiTicketTriageService>();

        try
        {
            var processed = await triage.ProcessQueueBatchAsync(batchSize, ct);
            var swept = await triage.SweepUnassignedAsync(TimeSpan.FromMinutes(retryAfterMinutes), batchSize, ct);

            context.Result = processed + swept;

            if (processed > 0 || swept > 0)
                logger.LogInformation("Triagem por IA: {Processed} triados, {Swept} atribuídos por fallback.", processed, swept);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao processar a fila da triagem por IA.");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
