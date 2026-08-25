using Discovery.Core.Cqrs.Clients.Commands;
using Discovery.Core.Cqrs.Clients.Queries;
using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.Cqrs.Notes.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Api.Filters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class ClientsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = "Medium")]
    public async Task<IActionResult> GetAll([FromQuery] bool includeInactive = false)
    {
        var result = await mediator.Send(new GetAllClientsQuery(includeInactive));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetClientByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : Problem(errors));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Clients, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateClientRequest request)
    {
        var cmd = new CreateClientCommand(request.Name, request.Notes);
        var result = await mediator.Send(cmd);
        return result.ToCreatedAtActionResult(nameof(GetById), new { id = result.Value!.Id }, this);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClientRequest request)
    {
        var cmd = new UpdateClientCommand(id, request.Name, request.Notes, request.IsActive);
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : Problem(errors));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await mediator.Send(new DeleteClientCommand(id));
        return NoContent();
    }

    [HttpGet("{id:guid}/notes/page")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetNotesPage(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new ListNotesPageQuery(id, null, null, cursor, limit), ct);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/notes")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> CreateNote(Guid id, [FromBody] CreateNoteRequest request, CancellationToken ct = default)
    {
        var source = HttpContext.Items["Username"] as string ?? "api";
        var cmd = new CreateNoteCommand(id, null, null, request.Content, source, request.IsPinned);
        var result = await mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetNoteById), new { id, noteId = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpGet("{id:guid}/notes/{noteId:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetNoteById(Guid id, Guid noteId)
    {
        var result = await mediator.Send(new GetNoteByIdQuery(noteId));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpPut("{id:guid}/notes/{noteId:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> UpdateNote(Guid id, Guid noteId, [FromBody] UpdateNoteRequest request, CancellationToken ct = default)
    {
        var cmd = new UpdateNoteCommand(noteId, request.Content, request.IsPinned);
        var result = await mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    [HttpDelete("{id:guid}/notes/{noteId:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> DeleteNote(Guid id, Guid noteId, CancellationToken ct = default)
    {
        var result = await mediator.Send(new DeleteNoteCommand(noteId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: _ => NotFound());
    }

    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetCustomFieldValues(Guid id, [FromQuery] bool includeSecrets = true, CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetClientCustomFieldsQuery(id, includeSecrets), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : Problem(errors));
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomFieldValue(
        Guid id, Guid definitionId,
        [FromBody] UpsertClientCustomFieldValueRequest request,
        CancellationToken ct = default)
    {
        var username = HttpContext.Items["Username"] as string ?? "api";
        var cmd = new UpsertClientCustomFieldCommand(id, definitionId, request.Value.GetRawText(), username);
        var result = await mediator.Send(cmd, ct);
        return result.ToActionResult();
    }

    private IActionResult Problem(IReadOnlyList<Discovery.Core.Cqrs.Error> errors)
    {
        return Problem(errors[0].Message, statusCode: 400);
    }
}

public record CreateClientRequest(string Name, string? Notes);
public record UpdateClientRequest(string Name, string? Notes, bool IsActive);
public record UpsertClientCustomFieldValueRequest(System.Text.Json.JsonElement Value);
