using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class TicketsController(
    IMediator mediator,
    ITicketWatcherRepository watcherRepo,
    ITicketRemoteSessionRepository remoteSessionRepo,
    ITicketAutomationLinkRepository automationLinkRepo,
    ITicketKnowledgeLinkRepository knowledgeLinkRepo,
    ITicketActivityLogRepository activityLogRepo,
    ITicketKpiCacheService kpiCache,
    ITicketRepository ticketRepo,
    ISlaService slaService,
    ICustomFieldService customFieldService,
    ITicketQueryService queryService) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [Obsolete("Use GET /page (cursor-based) em vez deste endpoint offset.")]
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] TicketFilterQuery filter)
    {
        var query = new ListTicketsQuery(filter);
        var result = await mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("page")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetPage([FromQuery] TicketFilterQuery filter)
    {
        var query = new ListTicketsQuery(filter);
        var result = await mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var query = new GetTicketByIdQuery(id);
        var result = await mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateTicketCommand command)
    {
        var result = await mediator.Send(command, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) })
        );
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketCommand command)
    {
        var cmd = command with { Id = id };
        var result = await mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) })
        );
    }

    [HttpPatch("{id:guid}/workflow-state")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpdateWorkflowState(Guid id, [FromBody] TransitionTicketStateCommand command)
    {
        var cmd = command with { TicketId = id };
        var result = await mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: r => Ok(new { message = "Workflow state updated", r.TicketId, r.PreviousStateId, r.NewStateId, r.ClosedAt }),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetComments(Guid id, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var commentQuery = new GetTicketCommentsQuery(id, cursor, Math.Clamp(limit, 1, 200));
        var result = await mediator.Send(commentQuery, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpPost("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddTicketCommentCommand command)
    {
        var cmd = command with { TicketId = id };
        var result = await mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: r => CreatedAtAction(nameof(GetComments), new { id }, r),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpPost("{id:guid}/merge")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> MergeTickets(Guid id, [FromBody] MergeTicketsCommand command)
    {
        var cmd = command with { TargetTicketId = id };
        var result = await mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("{id:guid}/sla")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetSlaStatus(Guid id)
    {
        var query = new GetTicketSlaStatusQuery(id);
        var result = await mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    // ── By Client ───────────────────────────────────────────────────────

    [HttpGet("by-client/{clientId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetByClient(Guid clientId, [FromQuery] int limit = 100)
    {
        var filter = new TicketFilterQuery(ClientId: clientId, Limit: Math.Clamp(limit, 1, 500));
        var page = await queryService.ListTicketsAsync(filter, HttpContext.RequestAborted);
        return Ok(page);
    }

    // ── Watchers ─────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/watchers")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetWatchers(Guid id)
    {
        var watchers = await watcherRepo.GetByTicketAsync(id);
        return Ok(watchers);
    }

    [HttpPost("{id:guid}/watchers")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> AddWatcher(Guid id, [FromBody] AddWatcherRequest request)
    {
        var watcher = await watcherRepo.AddAsync(id, request.UserId, Username);
        return CreatedAtAction(nameof(GetWatchers), new { id }, watcher);
    }

    [HttpDelete("{id:guid}/watchers/{userId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> RemoveWatcher(Guid id, Guid userId)
    {
        await watcherRepo.RemoveAsync(id, userId);
        return NoContent();
    }

    // ── Remote Sessions ──────────────────────────────────────────────────

    [HttpGet("{id:guid}/remote-sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetRemoteSessions(Guid id)
    {
        var sessions = await remoteSessionRepo.GetByTicketAsync(id, HttpContext.RequestAborted);
        return Ok(sessions);
    }

    [HttpPost("{id:guid}/remote-sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateRemoteSession(Guid id, [FromBody] TicketRemoteSession session)
    {
        session.TicketId = id;
        var created = await remoteSessionRepo.CreateAsync(session, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(GetRemoteSessions), new { id }, created);
    }

    [HttpPatch("{id:guid}/remote-sessions/{sessionId:guid}/end")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> EndRemoteSession(Guid id, Guid sessionId)
    {
        var sessions = await remoteSessionRepo.GetByTicketAsync(id, HttpContext.RequestAborted);
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return NotFound();
        session.EndedAt = DateTime.UtcNow;
        var updated = await remoteSessionRepo.UpdateAsync(session, HttpContext.RequestAborted);
        return Ok(updated);
    }

    // ── Automation Links ─────────────────────────────────────────────────

    [HttpGet("{id:guid}/automation-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAutomationLinks(Guid id)
    {
        var links = await automationLinkRepo.GetByTicketAsync(id, HttpContext.RequestAborted);
        return Ok(links);
    }

    [HttpPost("{id:guid}/automation-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateAutomationLink(Guid id, [FromBody] TicketAutomationLink link)
    {
        link.TicketId = id;
        var created = await automationLinkRepo.CreateAsync(link, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(GetAutomationLinks), new { id }, created);
    }

    // ── Knowledge Links ──────────────────────────────────────────────────

    [HttpGet("{id:guid}/knowledge-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetKnowledgeLinks(Guid id)
    {
        var links = await knowledgeLinkRepo.GetByTicketAsync(id, HttpContext.RequestAborted);
        return Ok(links);
    }

    [HttpPost("{id:guid}/knowledge-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateKnowledgeLink(Guid id, [FromBody] TicketKnowledgeLink link)
    {
        link.TicketId = id;
        var created = await knowledgeLinkRepo.CreateAsync(link, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(GetKnowledgeLinks), new { id }, created);
    }

    [HttpDelete("{id:guid}/knowledge-links/{linkId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> DeleteKnowledgeLink(Guid id, Guid linkId)
    {
        await knowledgeLinkRepo.DeleteAsync(linkId, HttpContext.RequestAborted);
        return NoContent();
    }

    // ── Audit Timeline ───────────────────────────────────────────────────

    [HttpGet("{id:guid}/audit/timeline")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAuditTimeline(Guid id)
    {
        var logs = await activityLogRepo.GetByTicketAsync(id);
        return Ok(logs);
    }

    // ── SLA Details ──────────────────────────────────────────────────────

    [HttpGet("{id:guid}/sla/details")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetSlaDetails(Guid id)
    {
        var (slaHours, slaPercent, slaBreached) = await slaService.GetSlaStatusAsync(id);
        var (frtHours, frtPercent, frtBreached, frtAchieved) = await slaService.GetFrtStatusAsync(id);
        return Ok(new
        {
            resolution = new { hoursRemaining = slaHours, percentUsed = slaPercent, breached = slaBreached },
            firstResponse = new { hoursRemaining = frtHours, percentUsed = frtPercent, breached = frtBreached, achieved = frtAchieved }
        });
    }

    // ── Custom Fields ────────────────────────────────────────────────────

    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetCustomFields(Guid id, [FromQuery] bool includeSecrets = false)
    {
        var values = await customFieldService.GetValuesAsync(
            CustomFieldScopeType.Ticket, id, includeSecrets, HttpContext.RequestAborted);
        return Ok(values);
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomField(Guid id, Guid definitionId, [FromBody] JsonElement body)
    {
        var valueJson = body.TryGetProperty("value", out var prop) ? prop.GetRawText() : body.GetRawText();
        var input = new UpsertCustomFieldValueInput(
            definitionId, CustomFieldScopeType.Ticket, id, valueJson, Username);
        var result = await customFieldService.UpsertValueAsync(input, HttpContext.RequestAborted);
        return Ok(result);
    }

    // ── KPI ──────────────────────────────────────────────────────────────

    [HttpGet("kpi")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetKpi(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] DateTime? since = null)
    {
        var result = await kpiCache.GetOrComputeAsync(
            clientId, departmentId, since,
            () => ticketRepo.GetKpiAsync(clientId, departmentId, since),
            HttpContext.RequestAborted);
        return Ok(result);
    }
}

public record AddWatcherRequest(Guid UserId);
