using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ISoftwareInventoryService
{
    Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentSoftwareSnapshot>> GetSnapshotsByAgentIdAsync(Guid agentId, CancellationToken ct = default);
}
