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
    Task<IReadOnlyList<string>> GetDistinctLabelsAsync();
    Task<AgentLabel?> GetByIdAsync(Guid id);
    Task<AgentLabel> AddAsync(AgentLabel label);
    Task DeleteAsync(Guid id);
}
