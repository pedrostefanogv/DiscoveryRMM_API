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

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class TicketsController(
    IMediator mediator,
    ISlaService slaService,
    ICustomFieldService customFieldService,
    ITicketQueryService queryService) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    // ── Listagem com paginação cursor ─────────────────────────────────

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] TicketFilterQuery filter)
    {
        var result = await mediator.Send(new ListTicketsQuery(filter), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetTicketByIdQuery(id), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateTicketCommand command)
    {
        var result = await mediator.Send(command, HttpContext.RequestAborted);
        return result.ToCreatedAtActionResult(nameof(GetById), new { id = result.Value!.Id }, this);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketCommand command)
    {
        var result = await mediator.Send(command with { Id = id }, HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPatch("{id:guid}/workflow-state")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpdateWorkflowState(Guid id, [FromBody] TransitionTicketStateCommand command)
    {
        var result = await mediator.Send(command with { TicketId = id }, HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetComments(Guid id, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetTicketCommentsQuery(id, cursor, Math.Clamp(limit, 1, 200)), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddTicketCommentCommand command)
    {
        var result = await mediator.Send(command with { TicketId = id }, HttpContext.RequestAborted);
        return result.Match<IActionResult>(success: r => CreatedAtAction(nameof(GetComments), new { id }, r), failure: NotFound);
    }

    [HttpPost("{id:guid}/merge")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> MergeTickets(Guid id, [FromBody] MergeTicketsCommand command)
    {
        var result = await mediator.Send(command with { TargetTicketId = id }, HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/sla")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetSlaStatus(Guid id)
    {
        var result = await mediator.Send(new GetTicketSlaStatusQuery(id), HttpContext.RequestAborted);
        return result.ToActionResult();
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
        var result = await mediator.Send(new GetTicketWatchersQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/watchers")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> AddWatcher(Guid id, [FromBody] AddWatcherRequest request)
    {
        var result = await mediator.Send(new AddTicketWatcherCommand(id, request.UserId, Username));
        return result.Match<IActionResult>(success: w => CreatedAtAction(nameof(GetWatchers), new { id }, w), failure: BadRequest);
    }

    [HttpDelete("{id:guid}/watchers/{userId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> RemoveWatcher(Guid id, Guid userId)
    {
        await mediator.Send(new RemoveTicketWatcherCommand(id, userId));
        return NoContent();
    }

    // ── Remote Sessions ──────────────────────────────────────────────────

    [HttpGet("{id:guid}/remote-sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetRemoteSessions(Guid id)
    {
        var result = await mediator.Send(new GetTicketRemoteSessionsQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/remote-sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateRemoteSession(Guid id, [FromBody] TicketRemoteSession body)
    {
        var result = await mediator.Send(new CreateTicketRemoteSessionCommand(id, body.AgentId, body.MeshNodeId, Username, body.Note));
        return result.Match<IActionResult>(success: s => CreatedAtAction(nameof(GetRemoteSessions), new { id }, s), failure: BadRequest);
    }

    [HttpPatch("{id:guid}/remote-sessions/{sessionId:guid}/end")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> EndRemoteSession(Guid id, Guid sessionId)
    {
        var result = await mediator.Send(new EndTicketRemoteSessionCommand(id, sessionId));
        return result.ToActionResult();
    }

    // ── Automation Links ─────────────────────────────────────────────────

    [HttpGet("{id:guid}/automation-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAutomationLinks(Guid id)
    {
        var result = await mediator.Send(new GetTicketAutomationLinksQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/automation-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateAutomationLink(Guid id, [FromBody] TicketAutomationLink body)
    {
        var result = await mediator.Send(new CreateTicketAutomationLinkCommand(id, body.AutomationTaskDefinitionId, Username, body.Note));
        return result.Match<IActionResult>(success: l => CreatedAtAction(nameof(GetAutomationLinks), new { id }, l), failure: BadRequest);
    }

    // ── Knowledge Links ──────────────────────────────────────────────────

    [HttpGet("{id:guid}/knowledge-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetKnowledgeLinks(Guid id)
    {
        var result = await mediator.Send(new GetTicketKnowledgeLinksQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/knowledge-links")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateKnowledgeLink(Guid id, [FromBody] TicketKnowledgeLink body)
    {
        var result = await mediator.Send(new CreateTicketKnowledgeLinkCommand(id, body.ArticleId, null, body.Note));
        return result.Match<IActionResult>(success: l => CreatedAtAction(nameof(GetKnowledgeLinks), new { id }, l), failure: BadRequest);
    }

    [HttpDelete("{id:guid}/knowledge-links/{linkId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> DeleteKnowledgeLink(Guid id, Guid linkId) { await mediator.Send(new DeleteTicketKnowledgeLinkCommand(linkId)); return NoContent(); }

    // ── Attachments ─────────────────────────────────────────────────────

    /// <summary>
    /// Lista anexos de um ticket com paginação por cursor.
    /// </summary>
    [HttpGet("{id:guid}/attachments")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAttachments(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetTicketAttachmentsQuery(id, cursor, limit));
        return result.ToActionResult();
    }

    // ── Audit Timeline ───────────────────────────────────────────────────

    [HttpGet("{id:guid}/audit/timeline")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAuditTimeline(Guid id)
    {
        var result = await mediator.Send(new GetTicketAuditTimelineQuery(id));
        return result.ToActionResult();
    }

    // ── SLA Details ──────────────────────────────────────────────────────

    [HttpGet("{id:guid}/sla/details")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetSlaDetails(Guid id)
    {
        var (slaHours, slaPercent, slaBreached) = await slaService.GetSlaStatusAsync(id);
        var (frtHours, frtPercent, frtBreached, frtAchieved) = await slaService.GetFrtStatusAsync(id);
        return Ok(new { resolution = new { hoursRemaining = slaHours, percentUsed = slaPercent, breached = slaBreached }, firstResponse = new { hoursRemaining = frtHours, percentUsed = frtPercent, breached = frtBreached, achieved = frtAchieved } });
    }

    // ── Custom Fields ────────────────────────────────────────────────────

    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetCustomFields(Guid id, [FromQuery] bool includeSecrets = false) => Ok(await customFieldService.GetValuesAsync(CustomFieldScopeType.Ticket, id, includeSecrets, HttpContext.RequestAborted));

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomField(Guid id, Guid definitionId, [FromBody] JsonElement body)
    {
        // Aceita tanto o formato { "value": ... } quanto o valor cru (string, número, etc.)
        // enviado diretamente como corpo JSON. TryGetProperty só é seguro em objetos.
        var valueJson = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var prop)
            ? prop.GetRawText()
            : body.GetRawText();
        var result = await customFieldService.UpsertValueAsync(new UpsertCustomFieldValueInput(definitionId, CustomFieldScopeType.Ticket, id, valueJson, Username), HttpContext.RequestAborted);
        return Ok(result);
    }

    // ── KPI ──────────────────────────────────────────────────────────────

    [HttpGet("kpi")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetKpi([FromQuery] Guid? clientId = null, [FromQuery] Guid? departmentId = null, [FromQuery] DateTime? since = null)
    {
        var result = await mediator.Send(new GetTicketKpiQuery(clientId, departmentId, since));
        return result.ToActionResult();
    }

    private IActionResult BadRequest(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) });
    private IActionResult NotFound(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => errors[0].Code == "NotFound" ? NotFound() : BadRequest(errors);
}

public record AddWatcherRequest(Guid UserId);
