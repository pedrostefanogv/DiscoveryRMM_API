using Discovery.Core.Cqrs.AgentP2p.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-p2p")]
public class AgentP2pController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetOverview([FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null, [FromQuery] Guid? agentId = null)
    {
        var result = await mediator.Send(new GetP2pOverviewQuery(scope, tenantId, siteId, agentId));
        return result.ToActionResult();
    }
}
