using Discovery.Core.Cqrs.AgentInstall.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-install")]
public class AgentInstallController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetInstallUrl([FromQuery] Guid clientId, [FromQuery] Guid siteId, [FromQuery] string? platform = null, [FromQuery] string? architecture = null)
    {
        var result = await mediator.Send(new GetAgentInstallUrlQuery(clientId, siteId, platform, architecture));
        return result.ToActionResult();
    }
}
