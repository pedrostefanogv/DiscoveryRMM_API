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

        // Tamanho de lote, cota por cliente, intervalo e tentativas vêm da
        // configuração (global herdada por cliente), não de appsettings.
        await using var scope = scopeFactory.CreateAsyncScope();
        var triage = scope.ServiceProvider.GetRequiredService<IAiTicketTriageService>();

        try
        {
            // ?force=true no acionamento manual: ignora o vencimento do escopo.
            var force = context.MergedJobDataMap.ContainsKey("force")
                && context.MergedJobDataMap.GetBoolean("force");

            var result = await triage.ProcessDueAsync(ct, force);
            context.Result = result;

            if (result.ScopesProcessed > 0 || result.Triaged > 0 || result.Swept > 0)
            {
                logger.LogInformation(
                    "Triagem por IA: {Scopes} escopo(s) vencido(s), {Triaged} triado(s), {Swept} atribuído(s) por fallback em {Elapsed}ms.",
                    result.ScopesProcessed, result.Triaged, result.Swept, result.ElapsedMs);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao processar a fila da triagem por IA.");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
