using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface IAgentAutoLabelingService
{
    /// <param name="actor">Autor da acao para auditoria ("system" quando origem automatica).</param>
    Task EvaluateAgentAsync(Guid agentId, string reason, string? actor = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Avalia varios agentes em uma unica passagem. Substitui o loop de
    /// EvaluateAgentAsync que disparava ~5 queries + SaveChanges POR AGENTE quando
    /// um custom field de Site ou Cliente mudava.
    /// </summary>
    Task EvaluateAgentsAsync(IReadOnlyCollection<Guid> agentIds, string reason, string? actor = null, CancellationToken cancellationToken = default);

    Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default);

    Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, string? actor = null, CancellationToken cancellationToken = default);

    /// <summary>Reprocessa com reporte de progresso (usado pelo endpoint de acompanhamento).</summary>
    Task ReprocessAllAgentsAsync(
        string reason,
        int batchSize,
        IProgress<AgentLabelReprocessProgress>? progress,
        string? actor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciliacao INCREMENTAL: avalia apenas os agentes cujos dados mudaram desde a
    /// ultima passagem concluida (marca d'agua em Redis). Substitui a varredura da frota
    /// inteira no job periodico; uma regra nova/alterada dispara a passagem completa.
    /// </summary>
    Task ReprocessChangedAgentsAsync(
        string reason,
        int batchSize = 200,
        IProgress<AgentLabelReprocessProgress>? progress = null,
        string? actor = null,
        CancellationToken cancellationToken = default);

    Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Previa da regra para varios agentes em uma unica passagem (1 chamada da UI).</summary>
    Task<IReadOnlyList<AgentLabelRuleDryRunResponse>> DryRunBatchAsync(AgentLabelRuleDryRunBatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Previa de impacto de uma regra em uma amostra da frota.</summary>
    Task<AgentLabelRuleImpactResponse> EvaluateImpactAsync(AgentLabelRuleImpactRequest request, CancellationToken cancellationToken = default);
}
