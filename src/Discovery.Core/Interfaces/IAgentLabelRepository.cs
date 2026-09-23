using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentLabelRepository
{
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId);
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds);
    /// <summary>
    /// Conta e pagina os agentes de uma regra no banco. Antes a contagem era feita
    /// materializando a lista inteira em memoria (TotalAgents = agents.Count).
    /// </summary>
    Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(
        Guid ruleId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Ids dos agentes que possuem uma label (qualquer origem), com paginacao por cursor.
    /// Permite filtrar a lista de agentes por label no servidor, sem carregar as labels
    /// de toda a frota na UI (que era o limite de 500 do endpoint em lote).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(
        string label,
        Guid? afterAgentId,
        int limit,
        CancellationToken ct = default);

    /// <summary>Contagem de agentes distintos que possuem a label.</summary>
    Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default);

    /// <summary>Labels distintas com a contagem de agentes de cada uma.</summary>
    Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctLabelsAsync();
    Task<AgentLabel?> GetByIdAsync(Guid id);

    /// <summary>Registra (idempotente) que o usuario removeu manualmente esta label automatica.</summary>
    Task SuppressAutomaticLabelAsync(Guid agentId, string label, string? suppressedBy, CancellationToken ct = default);

    /// <summary>Remove a supressao quando o usuario volta a adicionar a label manualmente.</summary>
    Task ClearSuppressionAsync(Guid agentId, string label, CancellationToken ct = default);

    /// <summary>Lista as supressoes de um agente (com o nome da regra que produz a label).</summary>
    Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Libera uma supressao pelo Id. Retorna false quando nao existe.</summary>
    Task<bool> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default);
    Task<AgentLabel> AddAsync(AgentLabel label);
    Task DeleteAsync(Guid id);
}
