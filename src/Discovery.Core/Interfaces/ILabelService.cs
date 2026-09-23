using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ILabelService
{
    // Labels
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Labels de varios agentes em uma unica consulta.</summary>
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctLabelsAsync(CancellationToken ct = default);
    Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default);
    /// <summary>Retorna false quando a label nao existe (permite responder 404).</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    // Rules
    Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default);
    Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default);
    Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default);
    Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, CancellationToken ct = default);

    // Agents matched by a rule
    Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(
        Guid ruleId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}
