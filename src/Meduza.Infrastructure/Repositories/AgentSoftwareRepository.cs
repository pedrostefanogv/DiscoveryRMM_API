using System.Security.Cryptography;
using System.Text;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AgentSoftwareRepository : IAgentSoftwareRepository
{
    private readonly MeduzaDbContext _db;

    public AgentSoftwareRepository(MeduzaDbContext db) => _db = db;

    public async Task<IEnumerable<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId)
    {
        return await (
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join catalog in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals catalog.Id
            where inv.AgentId == agentId && inv.IsPresent
            orderby catalog.Name
            select new AgentInstalledSoftware
            {
                InventoryId = inv.Id,
                AgentId = inv.AgentId,
                SoftwareId = inv.SoftwareId,
                Name = catalog.Name,
                Version = inv.Version,
                Publisher = catalog.Publisher,
                InstallId = catalog.InstallId,
                Serial = catalog.Serial,
                Source = catalog.Source,
                CollectedAt = inv.CollectedAt,
                FirstSeenAt = inv.FirstSeenAt,
                LastSeenAt = inv.LastSeenAt
            }).ToListAsync();
    }

    public async Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdPagedAsync(
        Guid agentId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
    {
        var pattern = BuildSearchPattern(search);
        var safeLimit = Math.Clamp(limit, 1, 500);

        var query =
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join catalog in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals catalog.Id
            where inv.AgentId == agentId && inv.IsPresent
            select new { inv, catalog };

        if (cursor.HasValue)
        {
            query = descending
                ? query.Where(x => x.inv.Id.CompareTo(cursor.Value) < 0)
                : query.Where(x => x.inv.Id.CompareTo(cursor.Value) > 0);
        }

        if (pattern is not null)
        {
            query = query.Where(x =>
                (x.inv.Version != null && EF.Functions.ILike(x.inv.Version, pattern)) ||
                EF.Functions.ILike(x.catalog.Name, pattern) ||
                (x.catalog.Publisher != null && EF.Functions.ILike(x.catalog.Publisher, pattern)) ||
                (x.catalog.InstallId != null && EF.Functions.ILike(x.catalog.InstallId, pattern)) ||
                (x.catalog.Serial != null && EF.Functions.ILike(x.catalog.Serial, pattern)) ||
                (x.catalog.Source != null && EF.Functions.ILike(x.catalog.Source, pattern)));
        }

        query = descending
            ? query.OrderByDescending(x => x.inv.Id)
            : query.OrderBy(x => x.inv.Id);

        return await query
            .Take(safeLimit)
            .Select(x => new AgentInstalledSoftware
            {
                InventoryId = x.inv.Id,
                AgentId = x.inv.AgentId,
                SoftwareId = x.inv.SoftwareId,
                Name = x.catalog.Name,
                Version = x.inv.Version,
                Publisher = x.catalog.Publisher,
                InstallId = x.catalog.InstallId,
                Serial = x.catalog.Serial,
                Source = x.catalog.Source,
                CollectedAt = x.inv.CollectedAt,
                FirstSeenAt = x.inv.FirstSeenAt,
                LastSeenAt = x.inv.LastSeenAt
            })
            .ToListAsync();
    }

    public async Task<AgentSoftwareSnapshot> GetSnapshotByAgentIdAsync(Guid agentId)
    {
        var snapshot = await _db.AgentSoftwareInventories
            .AsNoTracking()
            .Where(inv => inv.AgentId == agentId && inv.IsPresent)
            .GroupBy(_ => 1)
            .Select(group => new AgentSoftwareSnapshot
            {
                AgentId = agentId,
                TotalInstalled = group.Count(),
                FirstSeenAt = group.Min(x => x.FirstSeenAt),
                LastCollectedAt = group.Max(x => x.CollectedAt),
                LastSeenAt = group.Max(x => x.LastSeenAt),
                UpdatedAt = group.Max(x => x.UpdatedAt)
            })
            .FirstOrDefaultAsync();

        if (snapshot is not null)
            return snapshot;

        var lastUpdatedAt = await _db.AgentSoftwareInventories
            .AsNoTracking()
            .Where(inv => inv.AgentId == agentId)
            .MaxAsync(inv => (DateTime?)inv.UpdatedAt);

        return new AgentSoftwareSnapshot
        {
            AgentId = agentId,
            TotalInstalled = 0,
            UpdatedAt = lastUpdatedAt
        };
    }

    public async Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryGlobalPagedAsync(
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
        => await GetInventoryPagedInternalAsync(null, null, cursor, limit, search, descending);

    public async Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryByClientPagedAsync(
        Guid clientId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
        => await GetInventoryPagedInternalAsync(clientId, null, cursor, limit, search, descending);

    public async Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryBySitePagedAsync(
        Guid siteId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
        => await GetInventoryPagedInternalAsync(null, siteId, cursor, limit, search, descending);

    public async Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogGlobalPagedAsync(
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
        => await GetInventoryCatalogPagedInternalAsync(null, cursor, limit, search, descending);

    public async Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogByClientPagedAsync(
        Guid clientId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
        => await GetInventoryCatalogPagedInternalAsync(clientId, cursor, limit, search, descending);

    public async Task<SoftwareInventoryScopeSnapshot> GetInventoryGlobalSnapshotAsync()
        => await GetInventorySnapshotInternalAsync(null, null);

    public async Task<SoftwareInventoryScopeSnapshot> GetInventoryByClientSnapshotAsync(Guid clientId)
        => await GetInventorySnapshotInternalAsync(clientId, null);

    public async Task<SoftwareInventoryScopeSnapshot> GetInventoryBySiteSnapshotAsync(Guid siteId)
        => await GetInventorySnapshotInternalAsync(null, siteId);

    public async Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareGlobalAsync(int limit)
        => await GetTopSoftwareInternalAsync(null, null, limit);

    public async Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareBySiteAsync(Guid siteId, int limit)
        => await GetTopSoftwareInternalAsync(null, siteId, limit);

    public async Task ReplaceInventoryAsync(Guid agentId, DateTime collectedAt, IEnumerable<SoftwareInventoryEntry> software)
    {
        var normalized = software
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new SoftwareInventoryEntry
            {
                Name = entry.Name.Trim(),
                Version = TrimOrNull(entry.Version),
                Publisher = TrimOrNull(entry.Publisher),
                InstallId = TrimOrNull(entry.InstallId),
                Serial = TrimOrNull(entry.Serial),
                Source = TrimOrNull(entry.Source)
            })
            .ToList();

        var uniqueByFingerprint = normalized
            .GroupBy(BuildFingerprint)
            .Select(group => new { Fingerprint = group.Key, Item = group.First() })
            .ToList();

        var now = DateTime.UtcNow;

        await _db.AgentSoftwareInventories
            .Where(inv => inv.AgentId == agentId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(inv => inv.IsPresent, _ => false)
                .SetProperty(inv => inv.UpdatedAt, _ => now));

        var fingerprints = uniqueByFingerprint.Select(x => x.Fingerprint).ToList();

        var catalogs = await _db.SoftwareCatalogs
            .Where(catalog => fingerprints.Contains(catalog.Fingerprint))
            .ToDictionaryAsync(catalog => catalog.Fingerprint, catalog => catalog);

        foreach (var row in uniqueByFingerprint)
        {
            if (catalogs.ContainsKey(row.Fingerprint))
                continue;

            var catalog = new SoftwareCatalog
            {
                Id = IdGenerator.NewId(),
                Name = row.Item.Name,
                Publisher = row.Item.Publisher,
                InstallId = row.Item.InstallId,
                Serial = row.Item.Serial,
                Source = row.Item.Source,
                Fingerprint = row.Fingerprint,
                CreatedAt = now,
                UpdatedAt = now
            };

            catalogs[row.Fingerprint] = catalog;
            _db.SoftwareCatalogs.Add(catalog);
        }

        await _db.SaveChangesAsync();

        var softwareIds = catalogs.Values.Select(catalog => catalog.Id).ToList();

        var existingInventories = await _db.AgentSoftwareInventories
            .Where(inv => inv.AgentId == agentId && softwareIds.Contains(inv.SoftwareId))
            .ToDictionaryAsync(inv => inv.SoftwareId, inv => inv);

        foreach (var row in uniqueByFingerprint)
        {
            var softwareId = catalogs[row.Fingerprint].Id;

            if (!existingInventories.TryGetValue(softwareId, out var inventory))
            {
                _db.AgentSoftwareInventories.Add(new AgentSoftwareInventory
                {
                    Id = IdGenerator.NewId(),
                    AgentId = agentId,
                    SoftwareId = softwareId,
                    CollectedAt = collectedAt,
                    FirstSeenAt = collectedAt,
                    LastSeenAt = collectedAt,
                    Version = row.Item.Version,
                    IsPresent = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });

                continue;
            }

            inventory.CollectedAt = collectedAt;
            inventory.LastSeenAt = collectedAt;
            inventory.Version = row.Item.Version;
            inventory.IsPresent = true;
            inventory.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryPagedInternalAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
    {
        var pattern = BuildSearchPattern(search);
        var safeLimit = Math.Clamp(limit, 1, 500);

        var query =
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join agent in _db.Agents.AsNoTracking() on inv.AgentId equals agent.Id
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            join catalog in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals catalog.Id
            where inv.IsPresent
            select new { inv, agent, site, client, catalog };

        if (cursor.HasValue)
        {
            query = descending
                ? query.Where(x => x.inv.Id.CompareTo(cursor.Value) < 0)
                : query.Where(x => x.inv.Id.CompareTo(cursor.Value) > 0);
        }

        if (clientId.HasValue)
            query = query.Where(x => x.client.Id == clientId.Value);

        if (siteId.HasValue)
            query = query.Where(x => x.site.Id == siteId.Value);

        if (pattern is not null)
        {
            query = query.Where(x =>
                (x.inv.Version != null && EF.Functions.ILike(x.inv.Version, pattern)) ||
                EF.Functions.ILike(x.catalog.Name, pattern) ||
                (x.catalog.Publisher != null && EF.Functions.ILike(x.catalog.Publisher, pattern)) ||
                (x.catalog.InstallId != null && EF.Functions.ILike(x.catalog.InstallId, pattern)) ||
                (x.catalog.Serial != null && EF.Functions.ILike(x.catalog.Serial, pattern)) ||
                (x.catalog.Source != null && EF.Functions.ILike(x.catalog.Source, pattern)) ||
                EF.Functions.ILike(x.agent.Hostname, pattern) ||
                (x.agent.DisplayName != null && EF.Functions.ILike(x.agent.DisplayName, pattern)) ||
                EF.Functions.ILike(x.site.Name, pattern) ||
                EF.Functions.ILike(x.client.Name, pattern));
        }

        query = descending
            ? query.OrderByDescending(x => x.inv.Id)
            : query.OrderBy(x => x.inv.Id);

        return await query
            .Take(safeLimit)
            .Select(x => new SoftwareInventoryListItem
            {
                InventoryId = x.inv.Id,
                AgentId = x.inv.AgentId,
                SiteId = x.site.Id,
                ClientId = x.client.Id,
                SoftwareId = x.inv.SoftwareId,
                Name = x.catalog.Name,
                Version = x.inv.Version,
                Publisher = x.catalog.Publisher,
                InstallId = x.catalog.InstallId,
                Serial = x.catalog.Serial,
                Source = x.catalog.Source,
                Hostname = x.agent.Hostname,
                AgentDisplayName = x.agent.DisplayName,
                SiteName = x.site.Name,
                ClientName = x.client.Name,
                CollectedAt = x.inv.CollectedAt,
                FirstSeenAt = x.inv.FirstSeenAt,
                LastSeenAt = x.inv.LastSeenAt
            })
            .ToListAsync();
    }

    private async Task<SoftwareInventoryScopeSnapshot> GetInventorySnapshotInternalAsync(Guid? clientId, Guid? siteId)
    {
        var query =
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join agent in _db.Agents.AsNoTracking() on inv.AgentId equals agent.Id
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where inv.IsPresent
            select new { inv, site, client };

        if (clientId.HasValue)
            query = query.Where(x => x.client.Id == clientId.Value);

        if (siteId.HasValue)
            query = query.Where(x => x.site.Id == siteId.Value);

        var snapshot = await query
            .GroupBy(_ => 1)
            .Select(group => new SoftwareInventoryScopeSnapshot
            {
                TotalInstalled = group.Count(),
                DistinctSoftware = group.Select(x => x.inv.SoftwareId).Distinct().Count(),
                DistinctAgents = group.Select(x => x.inv.AgentId).Distinct().Count(),
                FirstSeenAt = group.Min(x => x.inv.FirstSeenAt),
                LastCollectedAt = group.Max(x => x.inv.CollectedAt),
                LastSeenAt = group.Max(x => x.inv.LastSeenAt),
                UpdatedAt = group.Max(x => x.inv.UpdatedAt)
            })
            .FirstOrDefaultAsync();

        return snapshot ?? new SoftwareInventoryScopeSnapshot();
    }

    private async Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogPagedInternalAsync(
        Guid? clientId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
    {
        var pattern = BuildSearchPattern(search);
        var safeLimit = Math.Clamp(limit, 1, 500);

        var query =
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join agent in _db.Agents.AsNoTracking() on inv.AgentId equals agent.Id
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            join catalog in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals catalog.Id
            where inv.IsPresent
            select new { inv, client, catalog };

        if (clientId.HasValue)
            query = query.Where(x => x.client.Id == clientId.Value);

        if (cursor.HasValue)
        {
            query = descending
                ? query.Where(x => x.catalog.Id.CompareTo(cursor.Value) < 0)
                : query.Where(x => x.catalog.Id.CompareTo(cursor.Value) > 0);
        }

        if (pattern is not null)
        {
            query = query.Where(x =>
                EF.Functions.ILike(x.catalog.Name, pattern) ||
                (x.catalog.Publisher != null && EF.Functions.ILike(x.catalog.Publisher, pattern)) ||
                (x.catalog.Source != null && EF.Functions.ILike(x.catalog.Source, pattern)));
        }

        var grouped = query
            .GroupBy(x => new { x.catalog.Id, x.catalog.Name, x.catalog.Publisher, x.catalog.Source })
            .Select(group => new SoftwareInventoryCatalogItem
            {
                SoftwareId = group.Key.Id,
                Name = group.Key.Name,
                Publisher = group.Key.Publisher,
                Source = group.Key.Source,
                InstalledCount = group.Count(),
                FirstSeenAt = group.Min(x => (DateTime?)x.inv.FirstSeenAt),
                LastCollectedAt = group.Max(x => (DateTime?)x.inv.CollectedAt),
                LastSeenAt = group.Max(x => (DateTime?)x.inv.LastSeenAt),
                UpdatedAt = group.Max(x => (DateTime?)x.inv.UpdatedAt)
            });

        grouped = descending
            ? grouped.OrderByDescending(x => x.SoftwareId)
            : grouped.OrderBy(x => x.SoftwareId);

        return await grouped
            .Take(safeLimit)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareInternalAsync(Guid? clientId, Guid? siteId, int limit)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);

        var query =
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join agent in _db.Agents.AsNoTracking() on inv.AgentId equals agent.Id
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            join catalog in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals catalog.Id
            where inv.IsPresent
            select new { inv, site, client, catalog };

        if (clientId.HasValue)
            query = query.Where(x => x.client.Id == clientId.Value);

        if (siteId.HasValue)
            query = query.Where(x => x.site.Id == siteId.Value);

        return await query
            .GroupBy(x => new { x.catalog.Id, x.catalog.Name, x.catalog.Publisher, x.catalog.Source })
            .Select(group => new SoftwareInventoryTopItem
            {
                SoftwareId = group.Key.Id,
                Name = group.Key.Name,
                Publisher = group.Key.Publisher,
                Source = group.Key.Source,
                InstalledCount = group.Count()
            })
            .OrderByDescending(x => x.InstalledCount)
            .ThenBy(x => x.Name)
            .Take(safeLimit)
            .ToListAsync();
    }

    private static string? TrimOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static string BuildFingerprint(SoftwareInventoryEntry item)
    {
        var input = string.Join("|", new[]
        {
            Normalize(item.Name),
            Normalize(item.Publisher),
            Normalize(item.InstallId),
            Normalize(item.Serial),
            Normalize(item.Source)
        });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? BuildSearchPattern(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;

        return $"%{search.Trim()}%";
    }
}
