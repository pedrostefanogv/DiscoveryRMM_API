using Discovery.Core.Cqrs.SoftwareInventory.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/software-inventory")]
public class SoftwareInventoryController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetByAgent([FromQuery] Guid agentId)
    {
        var result = await mediator.Send(new ListAgentSoftwareQuery(agentId));
        return result.ToActionResult();
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot()
    {
        var result = await mediator.Send(new GetSoftwareInventorySnapshotQuery());
        return result.ToActionResult();
    }
}
