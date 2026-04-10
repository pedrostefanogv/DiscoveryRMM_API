using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface ICommandRepository
{
    Task<AgentCommand?> GetByIdAsync(Guid id);
    Task<IEnumerable<AgentCommand>> GetPendingByAgentIdAsync(Guid agentId);
    Task<IEnumerable<AgentCommand>> GetByAgentIdAsync(Guid agentId, int limit = 50);
    Task<AgentCommand> CreateAsync(AgentCommand command);
    Task UpdateStatusAsync(Guid id, CommandStatus status, string? result, int? exitCode, string? errorMessage);
}
