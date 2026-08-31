using Discovery.Core.Cqrs.SoftwareInventory.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/software-inventory")]
public class SoftwareInventoryController(IMediator mediator) : ControllerBase
{
    /// <summary>Agent-scoped inventory (for agent detail page).</summary>
    [HttpGet("agent/{agentId:guid}")]
    public async Task<IActionResult> GetByAgent(Guid agentId)
    {
        var result = await mediator.Send(new ListAgentSoftwareQuery(agentId));
        return result.ToActionResult();
    }

    /// <summary>Scope-based inventory list (global, client, site).</summary>
    [HttpGet]
    public async Task<IActionResult> GetInventory(
        [FromQuery] SoftwareInventoryScope scope = SoftwareInventoryScope.Global,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 10,
        [FromQuery] string? search = null,
        [FromQuery] string order = "desc")
    {
        var descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
        var result = await mediator.Send(new ListSoftwareInventoryQuery(scope, scopeId, cursor, limit, search, descending));
        return result.ToActionResult();
    }

    /// <summary>Scope-based snapshot (cards at top of page).</summary>
    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(
        [FromQuery] SoftwareInventoryScope scope = SoftwareInventoryScope.Global,
        [FromQuery] Guid? scopeId = null)
    {
        var result = await mediator.Send(new GetSoftwareInventoryScopeSnapshotQuery(scope, scopeId));
        return result.ToActionResult();
    }

    /// <summary>Installations of a specific software, scoped (for details modal).</summary>
    [HttpGet("{softwareId:guid}/installations")]
    public async Task<IActionResult> GetInstallations(
        Guid softwareId,
        [FromQuery] SoftwareInventoryScope scope = SoftwareInventoryScope.Global,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        [FromQuery] string order = "asc")
    {
        var descending = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);
        var result = await mediator.Send(new ListSoftwareInstallationsQuery(softwareId, scope, scopeId, cursor, limit, descending));
        return result.ToActionResult();
    }
}
