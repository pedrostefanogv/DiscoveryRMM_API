using Meduza.Core.DTOs;

namespace Meduza.Core.Interfaces;

public interface IAgentAutoLabelingService
{
    Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default);
    Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default);
    Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, CancellationToken cancellationToken = default);
    Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default);
}
