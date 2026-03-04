using System.Security.Cryptography;
using System.Text;
using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class AgentSoftwareRepository : IAgentSoftwareRepository
{
    private readonly IDbConnectionFactory _db;

    public AgentSoftwareRepository(IDbConnectionFactory db) => _db = db;

    public async Task<IEnumerable<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<AgentInstalledSoftware>(
            """
            SELECT inv.id AS InventoryId, inv.agent_id AS AgentId, inv.software_id AS SoftwareId,
                     s.name, inv.version AS Version, s.publisher, s.install_id AS InstallId,
                   s.serial, s.source,
                   inv.collected_at AS CollectedAt, inv.first_seen_at AS FirstSeenAt,
                   inv.last_seen_at AS LastSeenAt
            FROM agent_software_inventory inv
            INNER JOIN software_catalog s ON s.id = inv.software_id
            WHERE inv.agent_id = @AgentId AND inv.is_present = @IsPresent
            ORDER BY s.name ASC
            """, new { AgentId = agentId, IsPresent = true });
    }

        public async Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdPagedAsync(
            Guid agentId,
            Guid? cursor,
            int limit,
            string? search,
            bool descending)
        {
            var searchLike = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";
            var comparison = descending ? "<" : ">";
            var direction = descending ? "DESC" : "ASC";

                using var conn = _db.CreateConnection();
                return (await conn.QueryAsync<AgentInstalledSoftware>(
                $"""
                        WITH page AS (
                                SELECT id, agent_id, software_id, collected_at, first_seen_at, last_seen_at, version
                                FROM agent_software_inventory
                                WHERE agent_id = @AgentId
                                    AND is_present = @IsPresent
                      AND (@Cursor IS NULL OR id {comparison} @Cursor)
                      AND (
                          @SearchLike IS NULL
                          OR version ILIKE @SearchLike
                          OR EXISTS (
                              SELECT 1
                              FROM software_catalog sc
                              WHERE sc.id = agent_software_inventory.software_id
                                AND (
                                    sc.name ILIKE @SearchLike
                                    OR sc.publisher ILIKE @SearchLike
                                    OR sc.install_id ILIKE @SearchLike
                                    OR sc.serial ILIKE @SearchLike
                                    OR sc.source ILIKE @SearchLike
                                )
                          )
                      )
                    ORDER BY id {direction}
                                LIMIT @Limit
                        )
                        SELECT page.id AS InventoryId, page.agent_id AS AgentId, page.software_id AS SoftwareId,
                                     s.name, page.version AS Version, s.publisher, s.install_id AS InstallId,
                                     s.serial, s.source,
                                     page.collected_at AS CollectedAt, page.first_seen_at AS FirstSeenAt,
                                     page.last_seen_at AS LastSeenAt
                        FROM page
                        INNER JOIN software_catalog s ON s.id = page.software_id
                ORDER BY page.id {direction}
                """, new { AgentId = agentId, IsPresent = true, Cursor = cursor, Limit = limit, SearchLike = searchLike })).ToList();
        }

        public async Task<AgentSoftwareSnapshot> GetSnapshotByAgentIdAsync(Guid agentId)
        {
            using var conn = _db.CreateConnection();
            var snapshot = await conn.QuerySingleOrDefaultAsync<AgentSoftwareSnapshot>(
                """
                SELECT @AgentId AS AgentId,
                       COALESCE(SUM(CASE WHEN is_present THEN 1 ELSE 0 END), 0) AS TotalInstalled,
                       MIN(CASE WHEN is_present THEN first_seen_at END) AS FirstSeenAt,
                       MAX(CASE WHEN is_present THEN collected_at END) AS LastCollectedAt,
                       MAX(CASE WHEN is_present THEN last_seen_at END) AS LastSeenAt,
                       MAX(updated_at) AS UpdatedAt
                FROM agent_software_inventory
                WHERE agent_id = @AgentId
                """, new { AgentId = agentId });

            return snapshot ?? new AgentSoftwareSnapshot { AgentId = agentId, TotalInstalled = 0 };
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
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new SoftwareInventoryEntry
            {
                Name = x.Name.Trim(),
                Version = TrimOrNull(x.Version),
                Publisher = TrimOrNull(x.Publisher),
                InstallId = TrimOrNull(x.InstallId),
                Serial = TrimOrNull(x.Serial),
                Source = TrimOrNull(x.Source)
            })
            .ToList();

        var uniqueByFingerprint = normalized
            .GroupBy(BuildFingerprint)
            .Select(g => new { Fingerprint = g.Key, Item = g.First() })
            .ToList();

        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var now = DateTime.UtcNow;

        await conn.ExecuteAsync(
            """
            UPDATE agent_software_inventory
            SET is_present = @IsPresent, updated_at = @Now
            WHERE agent_id = @AgentId
            """, new { AgentId = agentId, IsPresent = false, Now = now }, tx);

        foreach (var row in uniqueByFingerprint)
        {
            var softwareId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM software_catalog WHERE fingerprint = @Fingerprint",
                new { row.Fingerprint }, tx);

            if (!softwareId.HasValue)
            {
                softwareId = IdGenerator.NewId();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO software_catalog (id, name, version, publisher, install_id, serial, source,
                           fingerprint, created_at, updated_at)
                          VALUES (@Id, @Name, NULL, @Publisher, @InstallId, @Serial, @Source,
                           @Fingerprint, @CreatedAt, @UpdatedAt)
                    """,
                    new
                    {
                        Id = softwareId.Value,
                        row.Item.Name,
                        row.Item.Publisher,
                        row.Item.InstallId,
                        row.Item.Serial,
                        row.Item.Source,
                        Fingerprint = row.Fingerprint,
                        CreatedAt = now,
                        UpdatedAt = now
                    }, tx);
            }

            var inventoryId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM agent_software_inventory WHERE agent_id = @AgentId AND software_id = @SoftwareId",
                new { AgentId = agentId, SoftwareId = softwareId.Value }, tx);

            if (!inventoryId.HasValue)
            {
                inventoryId = IdGenerator.NewId();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO agent_software_inventory (id, agent_id, software_id, collected_at, first_seen_at,
                              last_seen_at, version, is_present, created_at, updated_at)
                          VALUES (@Id, @AgentId, @SoftwareId, @CollectedAt, @FirstSeenAt,
                              @LastSeenAt, @Version, @IsPresent, @CreatedAt, @UpdatedAt)
                    """,
                    new
                    {
                        Id = inventoryId.Value,
                        AgentId = agentId,
                        SoftwareId = softwareId.Value,
                        CollectedAt = collectedAt,
                        FirstSeenAt = collectedAt,
                        LastSeenAt = collectedAt,
                        Version = row.Item.Version,
                        IsPresent = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    }, tx);
            }
            else
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE agent_software_inventory
                    SET collected_at = @CollectedAt,
                        last_seen_at = @LastSeenAt,
                        version = @Version,
                        is_present = @IsPresent,
                        updated_at = @UpdatedAt
                    WHERE id = @Id
                    """,
                    new
                    {
                        Id = inventoryId.Value,
                        CollectedAt = collectedAt,
                        LastSeenAt = collectedAt,
                        Version = row.Item.Version,
                        IsPresent = true,
                        UpdatedAt = now
                    }, tx);
            }
        }

        tx.Commit();
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

    private async Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryPagedInternalAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
    {
        var searchLike = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";
        var comparison = descending ? "<" : ">";
        var direction = descending ? "DESC" : "ASC";

        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<SoftwareInventoryListItem>(
            $"""
            WITH page AS (
                SELECT inv.id, inv.agent_id, inv.software_id, inv.collected_at, inv.first_seen_at, inv.last_seen_at, inv.version
                FROM agent_software_inventory inv
                INNER JOIN agents a ON a.id = inv.agent_id
                INNER JOIN sites s ON s.id = a.site_id
                INNER JOIN clients c ON c.id = s.client_id
                INNER JOIN software_catalog sc ON sc.id = inv.software_id
                WHERE inv.is_present = @IsPresent
                  AND (@Cursor IS NULL OR inv.id {comparison} @Cursor)
                  AND (@ClientId IS NULL OR c.id = @ClientId)
                  AND (@SiteId IS NULL OR s.id = @SiteId)
                  AND (
                      @SearchLike IS NULL
                      OR inv.version ILIKE @SearchLike
                      OR sc.name ILIKE @SearchLike
                      OR sc.publisher ILIKE @SearchLike
                      OR sc.install_id ILIKE @SearchLike
                      OR sc.serial ILIKE @SearchLike
                      OR sc.source ILIKE @SearchLike
                      OR a.hostname ILIKE @SearchLike
                      OR a.display_name ILIKE @SearchLike
                      OR s.name ILIKE @SearchLike
                      OR c.name ILIKE @SearchLike
                  )
                ORDER BY inv.id {direction}
                LIMIT @Limit
            )
            SELECT page.id AS InventoryId,
                   page.agent_id AS AgentId,
                   a.site_id AS SiteId,
                   s.client_id AS ClientId,
                   page.software_id AS SoftwareId,
                   sc.name,
                   page.version AS Version,
                   sc.publisher,
                   sc.install_id AS InstallId,
                   sc.serial,
                   sc.source,
                   a.hostname,
                   a.display_name AS AgentDisplayName,
                   s.name AS SiteName,
                   c.name AS ClientName,
                   page.collected_at AS CollectedAt,
                   page.first_seen_at AS FirstSeenAt,
                   page.last_seen_at AS LastSeenAt
            FROM page
            INNER JOIN agents a ON a.id = page.agent_id
            INNER JOIN sites s ON s.id = a.site_id
            INNER JOIN clients c ON c.id = s.client_id
            INNER JOIN software_catalog sc ON sc.id = page.software_id
            ORDER BY page.id {direction}
            """,
            new
            {
                IsPresent = true,
                Cursor = cursor,
                ClientId = clientId,
                SiteId = siteId,
                SearchLike = searchLike,
                Limit = limit
            })).ToList();
    }

    private async Task<SoftwareInventoryScopeSnapshot> GetInventorySnapshotInternalAsync(Guid? clientId, Guid? siteId)
    {
        using var conn = _db.CreateConnection();
        var snapshot = await conn.QuerySingleOrDefaultAsync<SoftwareInventoryScopeSnapshot>(
            """
            SELECT COALESCE(COUNT(*), 0) AS TotalInstalled,
                   COALESCE(COUNT(DISTINCT inv.software_id), 0) AS DistinctSoftware,
                   COALESCE(COUNT(DISTINCT inv.agent_id), 0) AS DistinctAgents,
                   MIN(inv.first_seen_at) AS FirstSeenAt,
                   MAX(inv.collected_at) AS LastCollectedAt,
                   MAX(inv.last_seen_at) AS LastSeenAt,
                   MAX(inv.updated_at) AS UpdatedAt
            FROM agent_software_inventory inv
            INNER JOIN agents a ON a.id = inv.agent_id
            INNER JOIN sites s ON s.id = a.site_id
            INNER JOIN clients c ON c.id = s.client_id
            WHERE inv.is_present = @IsPresent
              AND (@ClientId IS NULL OR c.id = @ClientId)
              AND (@SiteId IS NULL OR s.id = @SiteId)
            """,
            new { IsPresent = true, ClientId = clientId, SiteId = siteId });

        return snapshot ?? new SoftwareInventoryScopeSnapshot();
    }

    private async Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogPagedInternalAsync(
        Guid? clientId,
        Guid? cursor,
        int limit,
        string? search,
        bool descending)
    {
        var searchLike = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";
        var comparison = descending ? "<" : ">";
        var direction = descending ? "DESC" : "ASC";

        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<SoftwareInventoryCatalogItem>(
            $"""
            WITH page AS (
                SELECT sc.id AS software_id,
                       sc.name,
                       sc.publisher,
                       sc.source,
                       COUNT(*)::int AS installed_count,
                       MIN(inv.first_seen_at) AS first_seen_at,
                       MAX(inv.collected_at) AS last_collected_at,
                       MAX(inv.last_seen_at) AS last_seen_at,
                       MAX(inv.updated_at) AS updated_at
                FROM agent_software_inventory inv
                INNER JOIN agents a ON a.id = inv.agent_id
                INNER JOIN sites s ON s.id = a.site_id
                INNER JOIN clients c ON c.id = s.client_id
                INNER JOIN software_catalog sc ON sc.id = inv.software_id
                WHERE inv.is_present = @IsPresent
                  AND (@ClientId IS NULL OR c.id = @ClientId)
                  AND (@Cursor IS NULL OR sc.id {comparison} @Cursor)
                  AND (
                      @SearchLike IS NULL
                      OR sc.name ILIKE @SearchLike
                      OR sc.publisher ILIKE @SearchLike
                      OR sc.source ILIKE @SearchLike
                  )
                GROUP BY sc.id, sc.name, sc.publisher, sc.source
                ORDER BY sc.id {direction}
                LIMIT @Limit
            )
            SELECT page.software_id AS SoftwareId,
                   page.name,
                   page.publisher,
                   page.source,
                   page.installed_count AS InstalledCount,
                   page.first_seen_at AS FirstSeenAt,
                   page.last_collected_at AS LastCollectedAt,
                   page.last_seen_at AS LastSeenAt,
                   page.updated_at AS UpdatedAt
            FROM page
            ORDER BY page.software_id {direction}
            """,
            new
            {
                IsPresent = true,
                ClientId = clientId,
                Cursor = cursor,
                SearchLike = searchLike,
                Limit = limit
            })).ToList();
    }

    private async Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareInternalAsync(Guid? clientId, Guid? siteId, int limit)
    {
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<SoftwareInventoryTopItem>(
            """
            SELECT inv.software_id AS SoftwareId,
                   sc.name,
                   sc.publisher,
                   sc.source,
                   COUNT(*)::int AS InstalledCount
            FROM agent_software_inventory inv
            INNER JOIN agents a ON a.id = inv.agent_id
            INNER JOIN sites s ON s.id = a.site_id
            INNER JOIN clients c ON c.id = s.client_id
            INNER JOIN software_catalog sc ON sc.id = inv.software_id
            WHERE inv.is_present = @IsPresent
              AND (@ClientId IS NULL OR c.id = @ClientId)
              AND (@SiteId IS NULL OR s.id = @SiteId)
            GROUP BY inv.software_id, sc.name, sc.publisher, sc.source
            ORDER BY COUNT(*) DESC, sc.name ASC
            LIMIT @Limit
            """,
            new
            {
                IsPresent = true,
                ClientId = clientId,
                SiteId = siteId,
                Limit = limit
            })).ToList();
    }
}
