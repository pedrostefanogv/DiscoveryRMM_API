using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ISoftwareInventoryService
{
    // Agent-scoped
    Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentSoftwareSnapshot>> GetSnapshotsByAgentIdAsync(Guid agentId, CancellationToken ct = default);

    // Scope-based global / client / site
    Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryPagedAsync(Guid? clientId, Guid? siteId, string? cursor, int limit, string? search, bool descending, CancellationToken ct = default);
    Task<SoftwareInventoryScopeSnapshot> GetInventorySnapshotAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default);
}
