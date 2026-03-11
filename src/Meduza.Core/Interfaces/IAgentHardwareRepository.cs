using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAgentHardwareRepository
{
    Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId);
    Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId);
    Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null);
}
