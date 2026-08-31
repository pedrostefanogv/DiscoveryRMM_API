using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public sealed class SoftwareInventoryService : ISoftwareInventoryService
{
    private readonly IAgentSoftwareRepository _repo;
    private readonly IRedisService _redis;
    private readonly ILogger<SoftwareInventoryService> _logger;
    private const int SnapshotCacheTtlSeconds = 60;
    private const string SnapshotCachePrefix = "software-inventory:snapshot:";

    public SoftwareInventoryService(
        IAgentSoftwareRepository repo,
        IRedisService redis,
        ILogger<SoftwareInventoryService> logger)
    {
        _repo = repo;
        _redis = redis;
        _logger = logger;
    }

    // ── Agent-scoped ──────────────────────────────────────────────────
    public async Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId, CancellationToken ct = default)
    { var items = await _repo.GetCurrentByAgentIdPagedAsync(agentId, null, 500, null, false); return items; }
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
        var cacheKey = BuildSnapshotKey(clientId, siteId);

        // Cache-aside com fallback silencioso: Redis indisponível não quebra a página.
        try
        {
            var cached = await _redis.GetAsync(cacheKey);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var snapshot = JsonSerializer.Deserialize<SoftwareInventoryScopeSnapshot>(cached);
                if (snapshot is not null)
                    return snapshot;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler snapshot de inventário do cache, computando direto");
        }

        SoftwareInventoryScopeSnapshot computed;
        if (clientId.HasValue)
            computed = await _repo.GetInventoryByClientSnapshotAsync(clientId.Value);
        else if (siteId.HasValue)
            computed = await _repo.GetInventoryBySiteSnapshotAsync(siteId.Value);
        else
            computed = await _repo.GetInventoryGlobalSnapshotAsync();

        try
        {
            await _redis.SetAsync(
                cacheKey,
                JsonSerializer.Serialize(computed),
                SnapshotCacheTtlSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar snapshot de inventário no cache");
        }

        return computed;
    }

    private static string BuildSnapshotKey(Guid? clientId, Guid? siteId)
    {
        if (clientId.HasValue)
            return $"{SnapshotCachePrefix}client:{clientId.Value:N}";
        if (siteId.HasValue)
            return $"{SnapshotCachePrefix}site:{siteId.Value:N}";
        return $"{SnapshotCachePrefix}global";
    }
}
