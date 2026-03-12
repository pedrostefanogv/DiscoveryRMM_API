namespace Meduza.Core.Interfaces;

public interface IAgentAutoLabelingService
{
    Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default);
    Task ReprocessAllAgentsAsync(string reason, CancellationToken cancellationToken = default);
}
