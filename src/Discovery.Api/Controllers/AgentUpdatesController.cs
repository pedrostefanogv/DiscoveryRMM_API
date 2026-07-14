using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-updates")]
public class AgentUpdatesController(IMediator mediator) : ControllerBase
{
    public sealed record RefreshBuildRequest(
        string? Version,
        string? Platform,
        string? Architecture,
        string? ArtifactType,
        string? SignatureThumbprint,
        string? Actor);

    [HttpGet("build/current")]
    public async Task<IActionResult> GetCurrentBuild([FromQuery] string? platform, [FromQuery] string? architecture, [FromQuery] string? artifactType, CancellationToken ct)
    {
        var r = await mediator.Send(new GetCurrentAgentBuildQuery(platform, architecture, artifactType), ct);
        return r.Match<IActionResult>(Ok, e => NotFound(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("build/refresh")]
    public async Task<IActionResult> RefreshBuild(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshBuildRequest? request,
        CancellationToken ct)
    {
        var cmd = new RebuildAgentCommand(
            request?.Version,
            request?.Platform,
            request?.Architecture,
            request?.ArtifactType,
            request?.SignatureThumbprint,
            request?.Actor);

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
