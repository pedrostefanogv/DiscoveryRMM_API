using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Cqrs.Agents.Automation.Queries;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Queries;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.Cqrs.Agents.Fanout.Commands;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.Cqrs.Agents.Maintenance.Commands;
using Discovery.Core.Cqrs.Agents.PowerManagement.Commands;
using Discovery.Core.Cqrs.Agents.RemoteDebug.Commands;
using Discovery.Core.Cqrs.Agents.Transfer.Commands;
using Discovery.Core.Cqrs.Notes.Queries;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class AgentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AgentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────

    [HttpPost("{agentId:guid}/approve-zero-touch")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> ApproveZeroTouch(Guid agentId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ApproveZeroTouchCommand(agentId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    [HttpGet("by-site/{siteId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetBySite(Guid siteId)
    {
        var result = await _mediator.Send(new GetAgentsBySiteQuery(siteId));
        return result.Match<IActionResult>(success: Ok, failure: _ => BadRequest());
    }

    [HttpGet("by-client/{clientId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetByClient(Guid clientId)
    {
        var result = await _mediator.Send(new GetAgentsByClientQuery(clientId));
        return result.Match<IActionResult>(success: Ok, failure: _ => BadRequest());
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _mediator.Send(new GetAgentByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpPost]
    [RequirePermission(ResourceType.Agents, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateAgentCommand cmd)
    {
        var result = await _mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAgentCommand cmd)
    {
        var result = await _mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediator.Send(new DeleteAgentCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetCustomFieldValues(Guid id, [FromQuery] bool includeSecrets = true, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetAgentCustomFieldsQuery(id, includeSecrets), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomFieldValue(Guid id, Guid definitionId, [FromBody] UpsertAgentCustomFieldCommand cmd, CancellationToken ct = default)
    {
        var result = await _mediator.Send(cmd with { AgentId = id, DefinitionId = definitionId }, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { error = errors[0].Message }));
    }

    // ── Inventory ─────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/hardware")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetHardware(Guid id)
    {
        var result = await _mediator.Send(new GetAgentHardwareQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpGet("{id:guid}/software")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetSoftware(Guid id, [FromQuery] string? cursor = null, [FromQuery] int limit = 100, [FromQuery] string? search = null, [FromQuery] string order = "asc")
    {
        var normalizedOrder = order.Trim().ToLowerInvariant();
        if (normalizedOrder is not ("asc" or "desc"))
            return BadRequest(new { error = "Invalid order. Use 'asc' or 'desc'." });

        var result = await _mediator.Send(new GetAgentSoftwareQuery(id, cursor, Math.Clamp(limit, 1, 500), search, normalizedOrder == "desc"));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpGet("{id:guid}/software/snapshot")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetSoftwareSnapshot(Guid id)
    {
        var result = await _mediator.Send(new GetAgentSoftwareSnapshotQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    // ── Automation ────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/automation/tasks/{taskId:guid}/run-now")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute)]
    public async Task<IActionResult> RunAutomationTaskNow(Guid id, Guid taskId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RunAutomationTaskCommand(id, taskId), ct);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetCommands), new { id }, dto),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("{id:guid}/automation/scripts/{scriptId:guid}/run-now")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute)]
    public async Task<IActionResult> RunAutomationScriptNow(Guid id, Guid scriptId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new RunAutomationScriptCommand(id, scriptId), ct);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetCommands), new { id }, dto),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("{id:guid}/automation/force-sync")]
    [RequirePermission(ResourceType.Automation, ActionType.Execute)]
    public async Task<IActionResult> ForceAutomationSync(Guid id, [FromBody] ForceAutomationSyncCommand cmd)
    {
        var result = await _mediator.Send(cmd with { AgentId = id });
        return result.Match<IActionResult>(
            success: _ => CreatedAtAction(nameof(GetCommands), new { id }, new { sync = "dispatched" }),
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("{id:guid}/refresh-data")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> RefreshAgentData(Guid id, [FromBody] RefreshAgentDataCommand cmd, CancellationToken ct = default)
    {
        var result = await _mediator.Send(cmd with { AgentId = id }, ct);
        return result.Match<IActionResult>(
            success: _ => Ok(new { success = true }),
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { error = errors[0].Message }));
    }

    [HttpGet("{id:guid}/automation/executions")]
    [RequirePermission(ResourceType.Automation, ActionType.View)]
    public async Task<IActionResult> GetAutomationExecutionHistory(Guid id, [FromQuery] int limit = 50)
    {
        var result = await _mediator.Send(new GetAutomationExecutionsQuery(id, limit));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    // ── Commands & Tokens ─────────────────────────────────────────────────

    [HttpGet("{id:guid}/commands")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetCommands(Guid id, [FromQuery] int limit = 50)
    {
        var result = await _mediator.Send(new GetAgentCommandsQuery(id, limit));
        return result.Match<IActionResult>(success: Ok, failure: _ => BadRequest());
    }

    [HttpPost("{id:guid}/commands")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> SendCommand(Guid id, [FromBody] SendAgentCommandCommand cmd)
    {
        var result = await _mediator.Send(cmd with { AgentId = id });
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetCommands), new { id }, dto),
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { error = errors[0].Message }));
    }

    [HttpGet("{id:guid}/tokens")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetTokens(Guid id)
    {
        var result = await _mediator.Send(new GetAgentTokensQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: _ => BadRequest());
    }

    [HttpPost("{id:guid}/tokens")]
    [RequirePermission(ResourceType.Agents, ActionType.Create)]
    public async Task<IActionResult> CreateToken(Guid id, [FromBody] CreateAgentTokenCommand cmd)
    {
        var result = await _mediator.Send(cmd with { AgentId = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { error = errors[0].Message }));
    }

    [HttpDelete("{id:guid}/tokens/{tokenId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Delete)]
    public async Task<IActionResult> RevokeToken(Guid id, Guid tokenId)
    {
        var result = await _mediator.Send(new RevokeAgentTokenCommand(id, tokenId));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: _ => BadRequest());
    }

    [HttpDelete("{id:guid}/tokens")]
    [RequirePermission(ResourceType.Agents, ActionType.Delete)]
    public async Task<IActionResult> RevokeAllTokens(Guid id)
    {
        var result = await _mediator.Send(new RevokeAllAgentTokensCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: _ => BadRequest());
    }

    // ── Fanout ────────────────────────────────────────────────────────────

    [HttpPost("commands/fanout/site/{siteId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> SendFanoutCommandToSite(Guid siteId, [FromBody] SendSiteFanoutCommand cmd, CancellationToken ct = default)
    {
        var result = await _mediator.Send(cmd with { SiteId = siteId }, ct);
        return result.Match<IActionResult>(
            success: dto => Accepted(dto),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message })
                : StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = errors[0].Message }));
    }

    [HttpPost("commands/fanout/client/{clientId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> SendFanoutCommandToClient(Guid clientId, [FromBody] SendClientFanoutCommand cmd, CancellationToken ct = default)
    {
        var result = await _mediator.Send(cmd with { ClientId = clientId }, ct);
        return result.Match<IActionResult>(
            success: dto => Accepted(dto),
            failure: errors => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = errors[0].Message }));
    }

    [HttpPost("commands/fanout/global")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> SendFanoutCommandGlobal([FromBody] SendGlobalFanoutCommand cmd, CancellationToken ct = default)
    {
        var result = await _mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: dto => Accepted(dto),
            failure: errors => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = errors[0].Message }));
    }

    // ── Maintenance ───────────────────────────────────────────────────────

    [HttpPatch("{id:guid}/maintenance")]
    public async Task<IActionResult> SetMaintenance(Guid id, [FromBody] SetAgentMaintenanceCommand cmd)
    {
        var result = await _mediator.Send(cmd with { AgentId = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message })
                : StatusCode(StatusCodes.Status403Forbidden, new { error = errors[0].Message }));
    }

    // ── Power Management ──────────────────────────────────────────────────

    [HttpPost("{id:guid}/restart")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> RestartAgent(Guid id, [FromBody] RestartAgentCommand cmd)
    {
        var result = await _mediator.Send(cmd with { AgentId = id });
        return result.Match<IActionResult>(
            success: _ => Accepted(new { agentId = id, commandType = "restart" }),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("{id:guid}/shutdown")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> ShutdownAgent(Guid id, [FromBody] ShutdownAgentCommand cmd)
    {
        var result = await _mediator.Send(cmd with { AgentId = id });
        return result.Match<IActionResult>(
            success: _ => Accepted(new { agentId = id, commandType = "shutdown" }),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("{id:guid}/wake-on-lan")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> WakeOnLan(Guid id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new WakeOnLanCommand(id), ct);
        return result.Match<IActionResult>(
            success: _ => Accepted(new { agentId = id, commandType = "wake-on-lan" }),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message })
                : errors[0].Code == "Validation" ? StatusCode(StatusCodes.Status412PreconditionFailed, new { error = errors[0].Message })
                : StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = errors[0].Message }));
    }

    // ── Remote Debug ──────────────────────────────────────────────────────

    [HttpPost("{id:guid}/remote-debug/start")]
    public async Task<IActionResult> StartRemoteDebug(Guid id, [FromBody] StartRemoteDebugCommand cmd)
    {
        if (HttpContext.Items["UserId"] is Guid userId)
            cmd = cmd with { AgentId = id, UserId = userId };
        else
            return Unauthorized(new { error = "User not authenticated." });

        var result = await _mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message })
                : errors[0].Code == "Forbidden" ? StatusCode(StatusCodes.Status403Forbidden, new { error = errors[0].Message })
                : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("{id:guid}/remote-debug/{sessionId:guid}/stop")]
    public async Task<IActionResult> StopRemoteDebug(Guid id, Guid sessionId)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var result = await _mediator.Send(new StopRemoteDebugCommand(id, sessionId, userId));
        return result.Match<IActionResult>(
            success: _ => Ok(new { sessionId, stoppedAtUtc = DateTime.UtcNow }),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message })
                : errors[0].Code == "Forbidden" ? StatusCode(StatusCodes.Status403Forbidden, new { error = errors[0].Message })
                : BadRequest(new { error = errors[0].Message }));
    }

    // ── Transfer ──────────────────────────────────────────────────────────

    [HttpPost("{agentId:guid}/transfer")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> TransferAgent(Guid agentId, [FromBody] TransferAgentCommand cmd, CancellationToken ct = default)
    {
        if (HttpContext.Items["UserId"] is Guid userId)
            cmd = cmd with { AgentId = agentId, UserId = userId };
        else
            return Unauthorized(new { error = "User not authenticated." });

        var result = await _mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "Forbidden" ? StatusCode(StatusCodes.Status403Forbidden, new { error = errors[0].Message })
                : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("transfer/bulk")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> BulkTransferAgents([FromBody] BulkTransferAgentsCommand cmd, CancellationToken ct = default)
    {
        if (HttpContext.Items["UserId"] is Guid userId)
            cmd = cmd with { UserId = userId };
        else
            return Unauthorized(new { error = "User not authenticated." });

        if (cmd.AgentIds is null || cmd.AgentIds.Count == 0)
            return BadRequest(new { error = "At least one agent ID is required." });
        if (cmd.AgentIds.Count > 100)
            return BadRequest(new { error = "Maximum of 100 agents per bulk transfer." });

        var result = await _mediator.Send(cmd, ct);
        return result.Match<IActionResult>(success: Ok, failure: _ => BadRequest());
    }

    [HttpGet("{agentId:guid}/validate-transfer")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> ValidateTransfer(Guid agentId, [FromQuery] Guid targetSiteId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ValidateAgentTransferQuery(agentId, targetSiteId), ct);
        return result.Match<IActionResult>(success: Ok, failure: _ => BadRequest());
    }

    /// <summary>
    /// GET /api/v1/agents/{id}/notes — retorna as notas do agente.
    /// Redireciona para o handler de ListNotesQuery com filtro por agentId.
    /// </summary>
    [HttpGet("{id:guid}/notes")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetNotes(Guid id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ListNotesQuery(null, null, id), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{id:guid}/notes/page")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetNotesPage(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ListNotesPageQuery(null, null, id, cursor, limit), ct);
        return result.ToActionResult();
    }
}