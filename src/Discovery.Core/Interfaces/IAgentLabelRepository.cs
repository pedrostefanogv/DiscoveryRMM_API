using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentLabelRepository
{
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId);
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds);
    Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId);
}
