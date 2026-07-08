using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ILabelService
{
    // Labels
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctLabelsAsync(CancellationToken ct = default);
    Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // Rules
    Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default);
    Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default);
    Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default);
    Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, CancellationToken ct = default);
}
