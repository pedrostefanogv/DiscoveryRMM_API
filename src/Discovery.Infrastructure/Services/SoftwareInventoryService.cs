using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class SoftwareInventoryService : ISoftwareInventoryService
{
    private readonly IAgentSoftwareRepository _repo;
    public SoftwareInventoryService(IAgentSoftwareRepository repo) => _repo = repo;

    // ── Agent-scoped ──────────────────────────────────────────────────
    public async Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId, CancellationToken ct = default)
    { var items = await _repo.GetCurrentByAgentIdPagedAsync(agentId, null, 200, null, false); return items; }
    public async Task<IReadOnlyList<AgentSoftwareSnapshot>> GetSnapshotsByAgentIdAsync(Guid agentId, CancellationToken ct = default)
    { var snapshot = await _repo.GetSnapshotByAgentIdAsync(agentId); return snapshot is null ? Array.Empty<AgentSoftwareSnapshot>() : new[] { snapshot }; }

    // ── Scope-based (catálogo agrupado por software) ─────────────────
    public async Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetCatalogPagedAsync(Guid? clientId, Guid? siteId, string? cursor, int limit, string? search, bool descending, CancellationToken ct = default)
    {
        if (clientId.HasValue)
            return await _repo.GetInventoryCatalogByClientPagedAsync(clientId.Value, cursor, limit, search, descending);
        if (siteId.HasValue)
            return await _repo.GetInventoryCatalogBySitePagedAsync(siteId.Value, cursor, limit, search, descending);
        return await _repo.GetInventoryCatalogGlobalPagedAsync(cursor, limit, search, descending);
    }

    public async Task<IReadOnlyList<SoftwareInstallationRow>> GetSoftwareInstallationsPagedAsync(Guid softwareId, Guid? clientId, Guid? siteId, string? cursor, int limit, bool descending, CancellationToken ct = default)
        => await _repo.GetSoftwareInstallationsPagedAsync(softwareId, clientId, siteId, cursor, limit, descending);

    public async Task<SoftwareInventoryScopeSnapshot> GetInventorySnapshotAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
    {
        if (clientId.HasValue)
            return await _repo.GetInventoryByClientSnapshotAsync(clientId.Value);
        if (siteId.HasValue)
            return await _repo.GetInventoryBySiteSnapshotAsync(siteId.Value);
        return await _repo.GetInventoryGlobalSnapshotAsync();
    }
}
