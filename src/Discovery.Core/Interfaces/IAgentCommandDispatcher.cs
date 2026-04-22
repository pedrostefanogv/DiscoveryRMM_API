using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentCommandDispatcher
{
    Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default);
}