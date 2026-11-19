using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public class AgentAuthAutomationController : AgentAuthControllerBase
{
    public AgentAuthAutomationController(IMediator mediator, IAgentRepository agentRepo)
        : base(mediator, agentRepo)
    {
    }

    [HttpPost("me/automation/policy-sync")]
    public async Task<IActionResult> SyncAutomationPolicy([FromBody] SyncAutomationPolicyCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), Ok);
    }

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentCommandsQuery(id, limit)), Ok);
    }

    [HttpPost("me/automation/executions/{commandId:guid}/ack")]
    public async Task<IActionResult> AckAutomationExecution(Guid commandId, [FromBody] AckAutomationExecutionCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, CommandId = commandId }), _ => Ok(new { acknowledged = true, commandId }));
    }

    [HttpPost("me/automation/executions/{commandId:guid}/result")]
    public async Task<IActionResult> CompleteAutomationExecution(Guid commandId, [FromBody] CompleteAutomationExecutionCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, CommandId = commandId }), _ => Ok(new { completed = true, commandId }));
    }
}
