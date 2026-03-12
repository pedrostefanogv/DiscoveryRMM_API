using Meduza.Core.DTOs;
using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAgentLabelRepository
{
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId);
    Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId);
}
