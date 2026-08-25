using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.Cqrs.Notes.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoint genérico de notas por entidade alvo (Client/Site/Agent).
/// A autorização é resolvida em runtime a partir do alvo da nota, pois o
/// tipo de recurso só é conhecido pelos IDs preenchidos (clientId/siteId/agentId).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/notes")]
public class NotesController(IMediator mediator, IPermissionService permission, INoteService noteService) : ControllerBase
{
    /// <summary>Username do usuário autenticado (ou fallback).</summary>
    private string CurrentUser => HttpContext.Items["Username"] as string ?? "api";

    private Guid? CurrentUserId => HttpContext.Items["UserId"] is Guid uid ? uid : null;

    private static bool TryGetResource(
        Guid? clientId, Guid? siteId, Guid? agentId,
        out ResourceType resource, out ScopeLevel scopeLevel, out Guid? scopeId, out Guid? parentScopeId)
    {
        if (agentId.HasValue)
        {
            resource = ResourceType.Agents; scopeLevel = ScopeLevel.Global; scopeId = null; parentScopeId = null;
        }
        else if (siteId.HasValue)
        {
            resource = ResourceType.Sites; scopeLevel = ScopeLevel.Site; scopeId = siteId; parentScopeId = clientId;
        }
        else if (clientId.HasValue)
        {
            resource = ResourceType.Clients; scopeLevel = ScopeLevel.Client; scopeId = clientId; parentScopeId = null;
        }
        else
        {
            resource = default; scopeLevel = default; scopeId = null; parentScopeId = null;
            return false;
        }
        return true;
    }

    /// <summary>Infere o alvo de uma nota a partir dos IDs de vínculo.</summary>
    private static (ResourceType resource, ScopeLevel scopeLevel, Guid? scopeId, Guid? parentScopeId)? ResolveTarget(Discovery.Core.Entities.EntityNote? note)
    {
        if (note is null)
            return null;

        Guid? clientId = note.ClientId, siteId = note.SiteId, agentId = note.AgentId;
        Guid? scopeId; ScopeLevel scopeLevel; Guid? parentScopeId; ResourceType resource;

        if (agentId.HasValue)
        {
            resource = ResourceType.Agents; scopeLevel = ScopeLevel.Global; scopeId = null; parentScopeId = null;
        }
        else if (siteId.HasValue)
        {
            resource = ResourceType.Sites; scopeLevel = ScopeLevel.Site; scopeId = siteId; parentScopeId = clientId;
        }
        else if (clientId.HasValue)
        {
            resource = ResourceType.Clients; scopeLevel = ScopeLevel.Client; scopeId = clientId; parentScopeId = null;
        }
        else
        {
            return null;
        }

        return (resource, scopeLevel, scopeId, parentScopeId);
    }

    private async Task<IActionResult?> EnforceAsync(ResourceType resource, ActionType action,
        ScopeLevel scopeLevel, Guid? scopeId, Guid? parentScopeId)
    {
        if (CurrentUserId is not Guid userId)
            return Unauthorized(new { message = "Autenticação necessária." });

        var ok = await permission.HasPermissionAsync(userId, resource, action, scopeLevel, scopeId, parentScopeId);
        if (!ok)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Permissão insuficiente." });
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> GetPage(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (!TryGetResource(clientId, siteId, agentId, out var resource, out var scopeLevel, out var scopeId, out var parentScopeId))
            return BadRequest(new { errors = new[] { new { code = "Validation", message = "Informe clientId, siteId ou agentId." } } });

        var denied = await EnforceAsync(resource, ActionType.View, scopeLevel, scopeId, parentScopeId);
        if (denied is not null) return denied;

        var result = await mediator.Send(new ListNotesPageQuery(clientId, siteId, agentId, cursor, limit), ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var note = await noteService.GetByIdAsync(id, ct);
        var target = ResolveTarget(note);
        if (target is null)
            return NotFound(new { errors = new[] { new { code = "NotFound", message = $"Note {id} not found" } } });

        var (resource, scopeLevel, scopeId, parentScopeId) = target.Value;
        var denied = await EnforceAsync(resource, ActionType.View, scopeLevel, scopeId, parentScopeId);
        if (denied is not null) return denied;

        var result = await mediator.Send(new GetNoteByIdQuery(id), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateNoteRequest request, CancellationToken ct = default)
    {
        if (!TryGetResource(request.ClientId, request.SiteId, request.AgentId, out var resource, out var scopeLevel, out var scopeId, out var parentScopeId))
            return BadRequest(new { errors = new[] { new { code = "Validation", message = "Informe clientId, siteId ou agentId." } } });

        var denied = await EnforceAsync(resource, ActionType.Edit, scopeLevel, scopeId, parentScopeId);
        if (denied is not null) return denied;

        var cmd = new CreateNoteCommand(
            request.ClientId, request.SiteId, request.AgentId,
            request.Content, CurrentUser, request.IsPinned);
        var result = await mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateNoteRequest request, CancellationToken ct = default)
    {
        var note = await noteService.GetByIdAsync(id, ct);
        var target = ResolveTarget(note);
        if (target is null)
            return NotFound(new { errors = new[] { new { code = "NotFound", message = $"Note {id} not found" } } });

        var (resource, scopeLevel, scopeId, parentScopeId) = target.Value;
        var denied = await EnforceAsync(resource, ActionType.Edit, scopeLevel, scopeId, parentScopeId);
        if (denied is not null) return denied;

        var cmd = new UpdateNoteCommand(id, request.Content, request.IsPinned);
        var result = await mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var note = await noteService.GetByIdAsync(id, ct);
        var target = ResolveTarget(note);
        if (target is null)
            return NotFound(new { errors = new[] { new { code = "NotFound", message = $"Note {id} not found" } } });

        var (resource, scopeLevel, scopeId, parentScopeId) = target.Value;
        var denied = await EnforceAsync(resource, ActionType.Edit, scopeLevel, scopeId, parentScopeId);
        if (denied is not null) return denied;

        var result = await mediator.Send(new DeleteNoteCommand(id), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
