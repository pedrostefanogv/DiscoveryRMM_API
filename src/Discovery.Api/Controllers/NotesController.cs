using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.Cqrs.Notes.Queries;
using Discovery.Core.Entities;
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
public class NotesController(IMediator mediator, IPermissionService permission, INoteService noteService, ISiteRepository siteRepository) : ControllerBase
{
    /// <summary>Username do usuário autenticado (ou fallback).</summary>
    private string CurrentUser => HttpContext.Items["Username"] as string ?? "api";

    private Guid? CurrentUserId => HttpContext.Items["UserId"] is Guid uid ? uid : null;

    private readonly record struct ResourceTarget(
        ResourceType Resource, ScopeLevel ScopeLevel, Guid? ScopeId, Guid? ParentScopeId);

    /// <summary>
    /// Resolve o recurso e o escopo do alvo da nota.
    /// Quando o alvo é um site e o clientId não foi informado, busca o
    /// clientId do site no repositório para permitir herança de permissão
    /// por Client (parentScopeId).
    /// </summary>
    private async Task<ResourceTarget?> ResolveResourceAsync(
        Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
    {
        var alvoCount = (clientId.HasValue ? 1 : 0) + (siteId.HasValue ? 1 : 0) + (agentId.HasValue ? 1 : 0);
        if (alvoCount != 1)
            return null;

        if (agentId.HasValue)
            return new ResourceTarget(ResourceType.Agents, ScopeLevel.Global, null, null);

        if (siteId.HasValue)
        {
            var parentClientId = clientId;
            if (!parentClientId.HasValue)
            {
                var site = await siteRepository.GetByIdAsync(siteId.Value);
                parentClientId = site?.ClientId;
            }
            return new ResourceTarget(ResourceType.Sites, ScopeLevel.Site, siteId, parentClientId);
        }

        return new ResourceTarget(ResourceType.Clients, ScopeLevel.Client, clientId, null);
    }

    /// <summary>Infere o alvo de uma nota a partir dos IDs de vínculo.</summary>
    private async Task<ResourceTarget?> ResolveTargetAsync(EntityNote? note, CancellationToken ct = default)
    {
        if (note is null)
            return null;

        if (note.AgentId.HasValue)
            return new ResourceTarget(ResourceType.Agents, ScopeLevel.Global, null, null);

        if (note.SiteId.HasValue)
        {
            var parentClientId = note.ClientId;
            if (!parentClientId.HasValue)
            {
                var site = await siteRepository.GetByIdAsync(note.SiteId.Value);
                parentClientId = site?.ClientId;
            }
            return new ResourceTarget(ResourceType.Sites, ScopeLevel.Site, note.SiteId, parentClientId);
        }

        if (note.ClientId.HasValue)
            return new ResourceTarget(ResourceType.Clients, ScopeLevel.Client, note.ClientId, null);

        return null;
    }

    private async Task<IActionResult?> EnforceAsync(ResourceTarget target, ActionType action)
    {
        if (CurrentUserId is not Guid userId)
            return Unauthorized(new { message = "Autenticação necessária." });

        var ok = await permission.HasPermissionAsync(
            userId, target.Resource, action, target.ScopeLevel, target.ScopeId, target.ParentScopeId);
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
        var target = await ResolveResourceAsync(clientId, siteId, agentId, ct);
        if (target is null)
            return BadRequest(new { errors = new[] { new { code = "Validation", message = "Informe exatamente um alvo (clientId, siteId ou agentId) para a nota." } } });

        var denied = await EnforceAsync(target.Value, ActionType.View);
        if (denied is not null) return denied;

        var result = await mediator.Send(new ListNotesPageQuery(clientId, siteId, agentId, cursor, limit), ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var note = await noteService.GetByIdAsync(id, ct);
        var target = await ResolveTargetAsync(note, ct);
        if (target is null)
            return NotFound(new { errors = new[] { new { code = "NotFound", message = $"Note {id} not found" } } });

        var denied = await EnforceAsync(target.Value, ActionType.View);
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
        var target = await ResolveResourceAsync(request.ClientId, request.SiteId, request.AgentId, ct);
        if (target is null)
            return BadRequest(new { errors = new[] { new { code = "Validation", message = "Informe exatamente um alvo (clientId, siteId ou agentId) para a nota." } } });

        var denied = await EnforceAsync(target.Value, ActionType.Edit);
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
        var target = await ResolveTargetAsync(note, ct);
        if (target is null)
            return NotFound(new { errors = new[] { new { code = "NotFound", message = $"Note {id} not found" } } });

        var denied = await EnforceAsync(target.Value, ActionType.Edit);
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
        var target = await ResolveTargetAsync(note, ct);
        if (target is null)
            return NotFound(new { errors = new[] { new { code = "NotFound", message = $"Note {id} not found" } } });

        var denied = await EnforceAsync(target.Value, ActionType.Edit);
        if (denied is not null) return denied;

        var result = await mediator.Send(new DeleteNoteCommand(id), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
