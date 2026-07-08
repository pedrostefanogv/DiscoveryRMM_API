using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class SoftwareInventoryService : ISoftwareInventoryService
{
    private readonly IAgentSoftwareRepository _repo;
    public SoftwareInventoryService(IAgentSoftwareRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId, CancellationToken ct = default)
    { var items = await _repo.GetCurrentByAgentIdPagedAsync(agentId, null, 200, null, false); return items; }
    public async Task<IReadOnlyList<AgentSoftwareSnapshot>> GetSnapshotsByAgentIdAsync(Guid agentId, CancellationToken ct = default)
    { var snapshot = await _repo.GetSnapshotByAgentIdAsync(agentId); return snapshot is null ? Array.Empty<AgentSoftwareSnapshot>() : new[] { snapshot }; }
}
