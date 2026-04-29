using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent hardware, software inventory, and software snapshot endpoints.
/// </summary>
public partial class AgentsController
{
    [HttpGet("{id:guid}/hardware")]
    public async Task<IActionResult> GetHardware(Guid id)
    {
        var cacheKey = $"agents:hardware:{id:N}";
        var payload = await GetOrSetCacheAsync(cacheKey, async () =>
        {
            var hardware = await _hardwareRepo.GetByAgentIdAsync(id);
            var components = await _hardwareRepo.GetComponentsAsync(id);
            return new AgentHardwareCachePayload(hardware, components.Disks, components.NetworkAdapters, components.MemoryModules, components.Printers, components.ListeningPorts, components.OpenSockets);
        }) ?? new AgentHardwareCachePayload(null, [], [], [], [], [], []);

        return Ok(new { Hardware = payload.Hardware, Disks = payload.Disks, NetworkAdapters = payload.NetworkAdapters, MemoryModules = payload.MemoryModules, Printers = payload.Printers, ListeningPorts = payload.ListeningPorts, OpenSockets = payload.OpenSockets });
    }

    [HttpGet("{id:guid}/software")]
    public async Task<IActionResult> GetSoftware(Guid id, [FromQuery] Guid? cursor = null, [FromQuery] int limit = 100, [FromQuery] string? search = null, [FromQuery] string order = "asc")
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var normalizedOrder = order.Trim().ToLowerInvariant();
        if (normalizedOrder is not ("asc" or "desc")) return BadRequest(new { error = "Invalid order. Use 'asc' or 'desc'." });

        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var descending = normalizedOrder == "desc";
        var page = await _softwareRepo.GetCurrentByAgentIdPagedAsync(id, cursor, normalizedLimit + 1, normalizedSearch, descending);

        var hasMore = page.Count > normalizedLimit;
        var items = hasMore ? page.Take(normalizedLimit).ToList() : page.ToList();
        var nextCursor = hasMore ? items[^1].InventoryId : (Guid?)null;

        return Ok(new { items, count = items.Count, cursor, nextCursor, hasMore, limit = normalizedLimit, search = normalizedSearch, order = normalizedOrder });
    }

    [HttpGet("{id:guid}/software/snapshot")]
    public async Task<IActionResult> GetSoftwareSnapshot(Guid id)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        var cacheKey = $"agents:software:snapshot:{id:N}";
        return Ok(await GetOrSetCacheAsync(cacheKey, async () => await _softwareRepo.GetSnapshotByAgentIdAsync(id)));
    }
}
