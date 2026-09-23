using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface IAgentAutoLabelingService
{
    Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default);
    Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default);

    Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, CancellationToken cancellationToken = default);

    /// <summary>Reprocessa com reporte de progresso (usado pelo endpoint de acompanhamento).</summary>
    Task ReprocessAllAgentsAsync(
        string reason,
        int batchSize,
        IProgress<AgentLabelReprocessProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Previa de impacto de uma regra em uma amostra da frota.</summary>
    Task<AgentLabelRuleImpactResponse> EvaluateImpactAsync(AgentLabelRuleImpactRequest request, CancellationToken cancellationToken = default);
}
