using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Ciclo de aprendizado da triagem por IA: extrai competências do histórico,
/// recalibra os pesos pela taxa de override e aplica/sugere conforme o modo do
/// departamento. Inerte quando nenhum departamento optou por aprender.
///
/// Configuração (appsettings):
/// - BackgroundJobs:AiAssignmentLearning:Enabled (default: true)
/// - BackgroundJobs:AiAssignmentLearning:HourUtc (default: 4)
/// </summary>
[DisallowConcurrentExecution]
public sealed class AiAssignmentLearningJob : IJob
{
    public static readonly JobKey Key = new("ai-assignment-learning", "tickets");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<AiAssignmentLearningJob>();
        var ct = context.CancellationToken;

        if (!(config.GetValue<bool?>("BackgroundJobs:AiAssignmentLearning:Enabled") ?? true))
        {
            logger.LogDebug("Aprendizado da triagem por IA desabilitado por configuração.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var learning = scope.ServiceProvider.GetRequiredService<IAiAssignmentLearningService>();

        try
        {
            var created = await learning.RunCycleAsync(null, null, ct);
            context.Result = created;

            if (created > 0)
                logger.LogInformation("Aprendizado da triagem por IA: {Count} sugestões/aplicações.", created);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha no ciclo de aprendizado da triagem por IA.");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
