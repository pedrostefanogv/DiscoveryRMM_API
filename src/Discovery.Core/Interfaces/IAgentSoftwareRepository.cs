using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentSoftwareRepository
{
    Task<IEnumerable<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId);

    /// <summary>Busca o software atual de varios agentes em uma unica query (evita N+1 no auto-labeling).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<AgentInstalledSoftware>>> GetCurrentByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default);
    Task<AgentInstalledSoftware?> GetByInventoryIdAsync(Guid inventoryId);
    Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdPagedAsync(
        Guid agentId,
        string? cursor,
        int limit,
        string? search,
        bool descending);
    Task<AgentSoftwarePageResult> GetCurrentByAgentIdOffsetAsync(
        Guid agentId,
        int page,
        int pageSize,
        string? search,
        bool descending,
        CancellationToken ct = default);
    Task<AgentSoftwareSnapshot> GetSnapshotByAgentIdAsync(Guid agentId);
    Task<int> GetUpdateAvailableCountByAgentIdAsync(Guid agentId);
    Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryGlobalPagedAsync(
        string? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryByClientPagedAsync(
        Guid clientId,
        string? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryBySitePagedAsync(
        Guid siteId,
        string? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogGlobalPagedAsync(
        string? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogByClientPagedAsync(
        Guid clientId,
        string? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogBySitePagedAsync(
        Guid siteId,
        string? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInstallationRow>> GetSoftwareInstallationsPagedAsync(
        Guid softwareId,
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        bool descending);
    Task<SoftwareInventoryScopeSnapshot> GetInventoryGlobalSnapshotAsync();
    Task<SoftwareInventoryScopeSnapshot> GetInventoryByClientSnapshotAsync(Guid clientId);
    Task<SoftwareInventoryScopeSnapshot> GetInventoryBySiteSnapshotAsync(Guid siteId);
    Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareGlobalAsync(int limit);
    Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareBySiteAsync(Guid siteId, int limit);
    Task ReplaceInventoryAsync(Guid agentId, DateTime collectedAt, IEnumerable<SoftwareInventoryEntry> software);
}
