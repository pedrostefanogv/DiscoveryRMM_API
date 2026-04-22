using System.Text.Json;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/software-inventory")]
public class SoftwareInventoryController : ControllerBase
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private const int CacheTtlSeconds = 30;

    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IRedisService _redisService;

    public SoftwareInventoryController(
        IAgentSoftwareRepository softwareRepo,
        IClientRepository clientRepo,
        ISiteRepository siteRepo,
        IRedisService redisService)
    {
        _softwareRepo = softwareRepo;
        _clientRepo = clientRepo;
        _siteRepo = siteRepo;
        _redisService = redisService;
    }

    [HttpGet]
    public async Task<IActionResult> GetGlobal(
        [FromQuery] Guid? cursor = null,
        [FromQuery] int limit = 100,
        [FromQuery] string? search = null,
        [FromQuery] string order = "asc")
    {
        if (!TryParseOrder(order, out var normalizedOrder, out var descending, out var badRequest))
            return badRequest!;

        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var cacheKey = BuildCacheKey("software-inventory:global:catalog", cursor, normalizedLimit, normalizedSearch, normalizedOrder);
        var result = await GetOrSetCacheAsync(cacheKey, async () =>
        {
            var page = await _softwareRepo.GetInventoryCatalogGlobalPagedAsync(cursor, normalizedLimit + 1, normalizedSearch, descending);
            var snapshot = await _softwareRepo.GetInventoryGlobalSnapshotAsync();
            return ToPagedResult(page, normalizedLimit, cursor, normalizedOrder, normalizedSearch, x => x.SoftwareId, snapshot);
        });

        return Ok(result);
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetGlobalSnapshot()
    {
        var snapshot = await GetOrSetCacheAsync("software-inventory:global:snapshot", () => _softwareRepo.GetInventoryGlobalSnapshotAsync());
        return Ok(snapshot);
    }

    [HttpGet("top")]
    public async Task<IActionResult> GetGlobalTop([FromQuery] int limit = 20)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var cacheKey = $"software-inventory:global:top:{normalizedLimit}";
        var result = await GetOrSetCacheAsync(cacheKey, async () =>
        {
            var items = await _softwareRepo.GetTopSoftwareGlobalAsync(normalizedLimit);
            return new TopSoftwareResult(items, items.Count, normalizedLimit);
        });

        return Ok(result);
    }

    [HttpGet("by-client/{clientId:guid}")]
    public async Task<IActionResult> GetByClient(
        Guid clientId,
        [FromQuery] Guid? cursor = null,
        [FromQuery] int limit = 100,
        [FromQuery] string? search = null,
        [FromQuery] string order = "asc")
    {
        var client = await _clientRepo.GetByIdAsync(clientId);
        if (client is null) return NotFound();

        if (!TryParseOrder(order, out var normalizedOrder, out var descending, out var badRequest))
            return badRequest!;

        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var cacheKey = BuildCacheKey($"software-inventory:client:{clientId:N}:catalog", cursor, normalizedLimit, normalizedSearch, normalizedOrder);
        var result = await GetOrSetCacheAsync(cacheKey, async () =>
        {
            var page = await _softwareRepo.GetInventoryCatalogByClientPagedAsync(clientId, cursor, normalizedLimit + 1, normalizedSearch, descending);
            var snapshot = await _softwareRepo.GetInventoryByClientSnapshotAsync(clientId);
            return ToPagedResult(page, normalizedLimit, cursor, normalizedOrder, normalizedSearch, x => x.SoftwareId, snapshot);
        });

        return Ok(result);
    }

    [HttpGet("by-client/{clientId:guid}/snapshot")]
    public async Task<IActionResult> GetByClientSnapshot(Guid clientId)
    {
        var client = await _clientRepo.GetByIdAsync(clientId);
        if (client is null) return NotFound();

        var snapshot = await GetOrSetCacheAsync(
            $"software-inventory:client:{clientId:N}:snapshot",
            () => _softwareRepo.GetInventoryByClientSnapshotAsync(clientId));
        return Ok(snapshot);
    }

    [HttpGet("by-site/{siteId:guid}")]
    public async Task<IActionResult> GetBySite(
        Guid siteId,
        [FromQuery] Guid? cursor = null,
        [FromQuery] int limit = 100,
        [FromQuery] string? search = null,
        [FromQuery] string order = "asc")
    {
        var site = await _siteRepo.GetByIdAsync(siteId);
        if (site is null) return NotFound();

        if (!TryParseOrder(order, out var normalizedOrder, out var descending, out var badRequest))
            return badRequest!;

        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var cacheKey = BuildCacheKey($"software-inventory:site:{siteId:N}:list", cursor, normalizedLimit, normalizedSearch, normalizedOrder);
        var result = await GetOrSetCacheAsync(cacheKey, async () =>
        {
            var page = await _softwareRepo.GetInventoryBySitePagedAsync(siteId, cursor, normalizedLimit + 1, normalizedSearch, descending);
            return ToPagedResult(page, normalizedLimit, cursor, normalizedOrder, normalizedSearch, x => x.InventoryId);
        });

        return Ok(result);
    }

    [HttpGet("by-site/{siteId:guid}/snapshot")]
    public async Task<IActionResult> GetBySiteSnapshot(Guid siteId)
    {
        var site = await _siteRepo.GetByIdAsync(siteId);
        if (site is null) return NotFound();

        var snapshot = await GetOrSetCacheAsync(
            $"software-inventory:site:{siteId:N}:snapshot",
            () => _softwareRepo.GetInventoryBySiteSnapshotAsync(siteId));
        return Ok(snapshot);
    }

    [HttpGet("by-site/{siteId:guid}/top")]
    public async Task<IActionResult> GetBySiteTop(Guid siteId, [FromQuery] int limit = 20)
    {
        var site = await _siteRepo.GetByIdAsync(siteId);
        if (site is null) return NotFound();

        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var cacheKey = $"software-inventory:site:{siteId:N}:top:{normalizedLimit}";
        var result = await GetOrSetCacheAsync(cacheKey, async () =>
        {
            var items = await _softwareRepo.GetTopSoftwareBySiteAsync(siteId, normalizedLimit);
            return new TopSoftwareResult(items, items.Count, normalizedLimit);
        });

        return Ok(result);
    }

    private async Task<T> GetOrSetCacheAsync<T>(string cacheKey, Func<Task<T>> factory)
    {
        var cached = await _redisService.GetAsync(cacheKey);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<T>(cached, CacheJsonOptions);
                if (deserialized is not null)
                    return deserialized;
            }
            catch (JsonException)
            {
                await _redisService.DeleteAsync(cacheKey);
            }
        }

        var value = await factory();
        var payload = JsonSerializer.Serialize(value, CacheJsonOptions);
        await _redisService.SetAsync(cacheKey, payload, CacheTtlSeconds);
        return value;
    }

    private static string BuildCacheKey(string prefix, Guid? cursor, int limit, string? search, string order)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? "-" : Uri.EscapeDataString(search);
        var cursorToken = cursor?.ToString("N") ?? "-";
        return $"{prefix}:cursor:{cursorToken}:limit:{limit}:search:{normalizedSearch}:order:{order}";
    }

    private static bool TryParseOrder(string order, out string normalizedOrder, out bool descending, out IActionResult? badRequest)
    {
        normalizedOrder = order.Trim().ToLowerInvariant();
        if (normalizedOrder is not ("asc" or "desc"))
        {
            descending = false;
            badRequest = new BadRequestObjectResult(new { error = "Invalid order. Use 'asc' or 'desc'." });
            return false;
        }

        descending = normalizedOrder == "desc";
        badRequest = null;
        return true;
    }

    private static PagedInventoryResult<T> ToPagedResult<T>(
        IReadOnlyList<T> page,
        int limit,
        Guid? cursor,
        string order,
        string? search,
        Func<T, Guid> getCursor,
        Discovery.Core.Entities.SoftwareInventoryScopeSnapshot? snapshot = null)
    {
        var hasMore = page.Count > limit;
        var items = hasMore ? page.Take(limit).ToList() : page.ToList();
        var nextCursor = hasMore ? getCursor(items[^1]) : (Guid?)null;

        return new PagedInventoryResult<T>(
            items,
            items.Count,
            snapshot?.TotalInstalled,
            snapshot?.DistinctSoftware,
            snapshot?.DistinctAgents,
            cursor,
            nextCursor,
            hasMore,
            limit,
            search,
            order);
    }

    private sealed record PagedInventoryResult<T>(
        IReadOnlyList<T> Items,
        int Count,
        int? TotalInstalled,
        int? TotalSoftware,
        int? TotalAgents,
        Guid? Cursor,
        Guid? NextCursor,
        bool HasMore,
        int Limit,
        string? Search,
        string Order);

    private sealed record TopSoftwareResult(
        IReadOnlyList<Discovery.Core.Entities.SoftwareInventoryTopItem> Items,
        int Count,
        int Limit);
}
