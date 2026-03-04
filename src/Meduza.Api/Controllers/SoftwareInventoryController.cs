using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/software-inventory")]
public class SoftwareInventoryController : ControllerBase
{
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ISiteRepository _siteRepo;

    public SoftwareInventoryController(
        IAgentSoftwareRepository softwareRepo,
        IClientRepository clientRepo,
        ISiteRepository siteRepo)
    {
        _softwareRepo = softwareRepo;
        _clientRepo = clientRepo;
        _siteRepo = siteRepo;
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
        var page = await _softwareRepo.GetInventoryCatalogGlobalPagedAsync(cursor, normalizedLimit + 1, normalizedSearch, descending);
        var snapshot = await _softwareRepo.GetInventoryGlobalSnapshotAsync();

        return Ok(ToPagedResult(page, normalizedLimit, cursor, normalizedOrder, normalizedSearch, x => x.SoftwareId, snapshot));
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetGlobalSnapshot()
    {
        var snapshot = await _softwareRepo.GetInventoryGlobalSnapshotAsync();
        return Ok(snapshot);
    }

    [HttpGet("top")]
    public async Task<IActionResult> GetGlobalTop([FromQuery] int limit = 20)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var items = await _softwareRepo.GetTopSoftwareGlobalAsync(normalizedLimit);
        return Ok(new { items, count = items.Count, limit = normalizedLimit });
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
        var page = await _softwareRepo.GetInventoryCatalogByClientPagedAsync(clientId, cursor, normalizedLimit + 1, normalizedSearch, descending);
        var snapshot = await _softwareRepo.GetInventoryByClientSnapshotAsync(clientId);

        return Ok(ToPagedResult(page, normalizedLimit, cursor, normalizedOrder, normalizedSearch, x => x.SoftwareId, snapshot));
    }

    [HttpGet("by-client/{clientId:guid}/snapshot")]
    public async Task<IActionResult> GetByClientSnapshot(Guid clientId)
    {
        var client = await _clientRepo.GetByIdAsync(clientId);
        if (client is null) return NotFound();

        var snapshot = await _softwareRepo.GetInventoryByClientSnapshotAsync(clientId);
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
        var page = await _softwareRepo.GetInventoryBySitePagedAsync(siteId, cursor, normalizedLimit + 1, normalizedSearch, descending);

        return Ok(ToPagedResult(page, normalizedLimit, cursor, normalizedOrder, normalizedSearch, x => x.InventoryId));
    }

    [HttpGet("by-site/{siteId:guid}/snapshot")]
    public async Task<IActionResult> GetBySiteSnapshot(Guid siteId)
    {
        var site = await _siteRepo.GetByIdAsync(siteId);
        if (site is null) return NotFound();

        var snapshot = await _softwareRepo.GetInventoryBySiteSnapshotAsync(siteId);
        return Ok(snapshot);
    }

    [HttpGet("by-site/{siteId:guid}/top")]
    public async Task<IActionResult> GetBySiteTop(Guid siteId, [FromQuery] int limit = 20)
    {
        var site = await _siteRepo.GetByIdAsync(siteId);
        if (site is null) return NotFound();

        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var items = await _softwareRepo.GetTopSoftwareBySiteAsync(siteId, normalizedLimit);
        return Ok(new { items, count = items.Count, limit = normalizedLimit });
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

    private static object ToPagedResult<T>(
        IReadOnlyList<T> page,
        int limit,
        Guid? cursor,
        string order,
        string? search,
        Func<T, Guid> getCursor,
        Meduza.Core.Entities.SoftwareInventoryScopeSnapshot? snapshot = null)
    {
        var hasMore = page.Count > limit;
        var items = hasMore ? page.Take(limit).ToList() : page.ToList();
        var nextCursor = hasMore ? getCursor(items[^1]) : (Guid?)null;

        return new
        {
            items,
            count = items.Count,
            totalInstalled = snapshot?.TotalInstalled,
            totalSoftware = snapshot?.DistinctSoftware,
            totalAgents = snapshot?.DistinctAgents,
            cursor,
            nextCursor,
            hasMore,
            limit,
            search,
            order
        };
    }
}
