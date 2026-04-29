using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que reconcilia labels de agentes com regras de auto-labeling configuradas.
/// Substitui AgentLabelingReconciliationBackgroundService.
/// Schedule: a cada 10 minutos.
/// </summary>
[DisallowConcurrentExecution]
public sealed class AgentLabelingReconciliationJob : IJob
{
    public static readonly JobKey Key = new("agent-labeling-reconciliation", "reconciliation");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<AgentLabelingReconciliationJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAgentAutoLabelingService>();

        if (!await service.HasEnabledRulesAsync(ct))
        {
            logger.LogDebug("Agent label reconciliation skipped — no enabled rules.");
            return;
        }

        try
        {
            await service.ReprocessAllAgentsAsync("periodic-reconciliation", cancellationToken: ct);
            logger.LogInformation("Agent label reconciliation finished.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent label reconciliation failed.");
        }
    }
}
