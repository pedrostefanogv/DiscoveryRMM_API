using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAgentHardwareRepository
{
    Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId);
    Task UpsertAsync(AgentHardwareInfo hardware);
    Task<IEnumerable<DiskInfo>> GetDisksAsync(Guid agentId);
    Task ReplaceDiskInfoAsync(Guid agentId, IEnumerable<DiskInfo> disks);
    Task<IEnumerable<NetworkAdapterInfo>> GetNetworkAdaptersAsync(Guid agentId);
    Task ReplaceNetworkAdaptersAsync(Guid agentId, IEnumerable<NetworkAdapterInfo> adapters);
    Task<IEnumerable<MemoryModuleInfo>> GetMemoryModulesAsync(Guid agentId);
    Task ReplaceMemoryModulesAsync(Guid agentId, IEnumerable<MemoryModuleInfo> modules);
}
