using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentLabelRuleRepository
{
    Task<IReadOnlyList<AgentLabelRule>> GetAllAsync(bool includeDisabled = true);
    Task<IReadOnlyList<AgentLabelRule>> GetEnabledAsync();
    Task<AgentLabelRule?> GetByIdAsync(Guid id);
    Task<AgentLabelRule> CreateAsync(AgentLabelRule rule);
    Task UpdateAsync(AgentLabelRule rule);
    Task DeleteAsync(Guid id);
}
