using Discovery.Core.Cqrs.Sites.Commands;
using Discovery.Core.Cqrs.Sites.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Api.Filters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

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
        return result.Match<IActionResult>(success: Ok, failure: Problem);
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
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { error = errors[0].Message }));
    }

    private IActionResult Problem(IReadOnlyList<Discovery.Core.Cqrs.Error> errors)
    {
        return Problem(errors[0].Message, statusCode: 400);
    }
}

public record CreateSiteRequest(string Name, string? Notes);
public record UpdateSiteRequest(string Name, string? Notes, bool IsActive);
public record UpsertSiteCustomFieldValueRequest(System.Text.Json.JsonElement Value);
