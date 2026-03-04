using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAgentSoftwareRepository
{
    Task<IEnumerable<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId);
    Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdPagedAsync(
        Guid agentId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending);
    Task<AgentSoftwareSnapshot> GetSnapshotByAgentIdAsync(Guid agentId);
    Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryGlobalPagedAsync(
        Guid? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryByClientPagedAsync(
        Guid clientId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryBySitePagedAsync(
        Guid siteId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogGlobalPagedAsync(
        Guid? cursor,
        int limit,
        string? search,
        bool descending);
    Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogByClientPagedAsync(
        Guid clientId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending);
    Task<SoftwareInventoryScopeSnapshot> GetInventoryGlobalSnapshotAsync();
    Task<SoftwareInventoryScopeSnapshot> GetInventoryByClientSnapshotAsync(Guid clientId);
    Task<SoftwareInventoryScopeSnapshot> GetInventoryBySiteSnapshotAsync(Guid siteId);
    Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareGlobalAsync(int limit);
    Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareBySiteAsync(Guid siteId, int limit);
    Task ReplaceInventoryAsync(Guid agentId, DateTime collectedAt, IEnumerable<SoftwareInventoryEntry> software);
}
