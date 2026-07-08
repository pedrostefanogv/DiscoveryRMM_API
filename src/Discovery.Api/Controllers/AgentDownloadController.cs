using Discovery.Core.Cqrs.AgentDownload.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-download")]
public class AgentDownloadController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetDownload([FromQuery] Guid agentId, [FromQuery] string? platform = null, [FromQuery] string? architecture = null)
    {
        var result = await mediator.Send(new GetAgentDownloadQuery(agentId, platform, architecture));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
