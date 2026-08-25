using Discovery.Core.Cqrs.Sites.Commands;
using Discovery.Core.Cqrs.Sites.Queries;
using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.Cqrs.Notes.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/clients/{clientId:guid}/[controller]")]
public class SitesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Sites, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetByClient(Guid clientId, [FromQuery] bool includeInactive = false)
    {
        var result = await mediator.Send(new GetSitesByClientQuery(clientId, includeInactive));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Sites, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetById(Guid clientId, Guid id)
    {
        var result = await mediator.Send(new GetSiteByIdQuery(clientId, id));
        return result.Match<IActionResult>(success: Ok, failure: _ => NotFound());
    }

    [HttpPost]
    [RequirePermission(ResourceType.Sites, ActionType.Create, ScopeSource.FromRoute)]
    public async Task<IActionResult> Create(Guid clientId, [FromBody] CreateSiteRequest request)
    {
        var cmd = new CreateSiteCommand(clientId, request.Name, request.Notes);
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: created => CreatedAtAction(nameof(GetById), new { clientId, id = created.Id }, created),
            failure: Problem);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Sites, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> Update(Guid clientId, Guid id, [FromBody] UpdateSiteRequest request)
    {
        var cmd = new UpdateSiteCommand(clientId, id, request.Name, request.Notes, request.IsActive);
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: Ok, failure: _ => NotFound());
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Sites, ActionType.Delete, ScopeSource.FromRoute)]
    public async Task<IActionResult> Delete(Guid clientId, Guid id)
    {
        var result = await mediator.Send(new DeleteSiteCommand(clientId, id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: _ => NotFound());
    }

    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Sites, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetCustomFieldValues(
        Guid clientId, Guid id, [FromQuery] bool includeSecrets = true, CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetSiteCustomFieldsQuery(clientId, id, includeSecrets), ct);
        return result.Match<IActionResult>(success: Ok, failure: _ => NotFound());
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Sites, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> UpsertCustomFieldValue(
        Guid clientId, Guid id, Guid definitionId,
        [FromBody] UpsertSiteCustomFieldValueRequest request, CancellationToken ct = default)
    {
        var username = HttpContext.Items["Username"] as string ?? "api";
        var cmd = new UpsertSiteCustomFieldCommand(clientId, id, definitionId, request.Value.GetRawText(), username);
        var result = await mediator.Send(cmd, ct);
        return result.ToActionResult();
    }

    // ── Notes ────────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/notes/page")]
    [RequirePermission(ResourceType.Sites, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetNotesPage(
        Guid clientId, Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new ListNotesPageQuery(null, id, null, cursor, limit), ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/notes/{noteId:guid}")]
    [RequirePermission(ResourceType.Sites, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetNoteById(Guid clientId, Guid id, Guid noteId)
    {
        var result = await mediator.Send(new GetNoteByIdQuery(noteId));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpPost("{id:guid}/notes")]
    [RequirePermission(ResourceType.Sites, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> CreateNote(
        Guid clientId, Guid id, [FromBody] CreateNoteRequest request, CancellationToken ct = default)
    {
        var source = HttpContext.Items["Username"] as string ?? "api";
        var cmd = new CreateNoteCommand(null, id, null, request.Content, source, request.IsPinned);
        var result = await mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetNoteById), new { clientId, id, noteId = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}/notes/{noteId:guid}")]
    [RequirePermission(ResourceType.Sites, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> UpdateNote(
        Guid clientId, Guid id, Guid noteId, [FromBody] UpdateNoteRequest request, CancellationToken ct = default)
    {
        var cmd = new UpdateNoteCommand(noteId, request.Content, request.IsPinned);
        var result = await mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpDelete("{id:guid}/notes/{noteId:guid}")]
    [RequirePermission(ResourceType.Sites, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> DeleteNote(
        Guid clientId, Guid id, Guid noteId, CancellationToken ct = default)
    {
        var result = await mediator.Send(new DeleteNoteCommand(noteId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: _ => NotFound());
    }

    private IActionResult Problem(IReadOnlyList<Discovery.Core.Cqrs.Error> errors)
    {
        return Problem(errors[0].Message, statusCode: 400);
    }
}

public record CreateSiteRequest(string Name, string? Notes);
public record UpdateSiteRequest(string Name, string? Notes, bool IsActive);
public record UpsertSiteCustomFieldValueRequest(System.Text.Json.JsonElement Value);
