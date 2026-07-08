using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-updates")]
public class AgentUpdatesController(IMediator mediator) : ControllerBase
{
    [HttpGet("build/current")]
    public async Task<IActionResult> GetCurrentBuild([FromQuery] string? platform, [FromQuery] string? architecture, [FromQuery] string? artifactType, CancellationToken ct)
    {
        var r = await mediator.Send(new GetCurrentAgentBuildQuery(platform, architecture, artifactType), ct);
        return r.Match<IActionResult>(Ok, e => NotFound(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("build/refresh")]
    public async Task<IActionResult> RefreshBuild([FromBody] RefreshAgentBuildCommand cmd, CancellationToken ct)
    {
        var r = await mediator.Send(cmd, ct);
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpGet("agents/{agentId:guid}/events")]
    public async Task<IActionResult> GetAgentEvents(Guid agentId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var r = await mediator.Send(new ListAgentUpdateEventsQuery(agentId, limit), ct);
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("agents/{agentId:guid}/force-update")]
    public async Task<IActionResult> ForceUpdate(Guid agentId, [FromBody] ForceAgentUpdateCommand cmd, CancellationToken ct)
    {
        var r = await mediator.Send(cmd with { AgentId = agentId }, ct);
        return r.Match<IActionResult>(_ => Ok(new { triggered = true }), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }
}
