using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentHardwareRepository
{
    Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId);
    Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId);
    Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null);
}
