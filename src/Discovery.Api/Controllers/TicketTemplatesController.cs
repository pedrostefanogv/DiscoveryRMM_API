using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Support.Templates;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-templates")]
public class TicketTemplatesController(IMediator mediator) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    private static async Task<IActionResult?> EnsureTemplateEditAccessAsync(
        Guid id,
        IScopeContext scopeContext,
        DiscoveryDbContext db,
        CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (access.HasGlobalAccess)
            return null;

        var existing = await db.TicketTemplates.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.ClientId })
            .FirstOrDefaultAsync(ct);
        if (existing is null)
            return new NotFoundObjectResult(new { error = "Template not found." });

        if (!existing.ClientId.HasValue || !access.AllowedClientIds.Contains(existing.ClientId.Value))
            return new ObjectResult(new { error = "Sem acesso para editar este template." }) { StatusCode = StatusCodes.Status403Forbidden };

        return null;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] bool includeGlobal = true,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] bool allClients = false)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess && clientId.HasValue && !access.AllowedClientIds.Contains(clientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

        var result = await mediator.Send(
            new ListTicketTemplatesQuery(clientId, departmentId, includeGlobal, includeInactive, includeDeleted, allClients),
            HttpContext.RequestAborted);
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        return Ok(result.Value!.Where(t => t.ClientId is null || access.AllowedClientIds.Contains(t.ClientId.Value)).ToList());
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTicketTemplateCommand cmd,
        [FromServices] IScopeContext scopeContext)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!access.HasGlobalAccess && (!cmd.ClientId.HasValue || !access.AllowedClientIds.Contains(cmd.ClientId.Value)))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso para criar template neste escopo." });

        return (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateTicketTemplateCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureTemplateEditAccessAsync(id, scopeContext, db, HttpContext.RequestAborted) is { } denied)
            return denied;

        return (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();
    }

    /// <summary>
    /// Sem permanent: soft delete (vai para a lixeira, restaurável).
    /// Com permanent=true: exclusão física; sem force=true recusa quando o
    /// template já foi usado por chamados (409 com a contagem).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] bool force = false,
        [FromQuery] bool permanent = false)
    {
        if (await EnsureTemplateEditAccessAsync(id, scopeContext, db, HttpContext.RequestAborted) is { } denied)
            return denied;

        var result = permanent
            ? await mediator.Send(new PurgeTicketTemplateCommand(id, force), HttpContext.RequestAborted)
            : await mediator.Send(new DeleteTicketTemplateCommand(id, Username), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    /// <summary>Tira o template da lixeira (volta a aparecer na listagem conforme IsActive).</summary>
    [HttpPost("{id:guid}/restore")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Restore(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureTemplateEditAccessAsync(id, scopeContext, db, HttpContext.RequestAborted) is { } denied)
            return denied;

        return (await mediator.Send(new RestoreTicketTemplateCommand(id), HttpContext.RequestAborted)).ToActionResult();
    }
}
