using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Support.Macros;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-macros")]
public class TicketMacrosController(IMediator mediator) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] bool includeGlobal = true)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess && clientId.HasValue && !access.AllowedClientIds.Contains(clientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

        var result = await mediator.Send(new ListTicketMacrosQuery(clientId, departmentId, includeGlobal), HttpContext.RequestAborted);
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        return Ok(result.Value!.Where(m => m.ClientId is null || access.AllowedClientIds.Contains(m.ClientId.Value)).ToList());
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTicketMacroCommand cmd,
        [FromServices] IScopeContext scopeContext)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!access.HasGlobalAccess && (!cmd.ClientId.HasValue || !access.AllowedClientIds.Contains(cmd.ClientId.Value)))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso para criar macro neste escopo." });

        return (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateTicketMacroCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!access.HasGlobalAccess)
        {
            var existing = await db.TicketMacros.AsNoTracking()
                .Where(m => m.Id == id)
                .Select(m => new { m.ClientId })
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (existing is null)
                return NotFound(new { error = "Macro not found." });
            if (!existing.ClientId.HasValue || !access.AllowedClientIds.Contains(existing.ClientId.Value))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso para editar esta macro." });
        }

        return (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!access.HasGlobalAccess)
        {
            var existing = await db.TicketMacros.AsNoTracking()
                .Where(m => m.Id == id)
                .Select(m => new { m.ClientId })
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (existing is null)
                return NotFound(new { error = "Macro not found." });
            if (!existing.ClientId.HasValue || !access.AllowedClientIds.Contains(existing.ClientId.Value))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso para excluir esta macro." });
        }

        return (await mediator.Send(new DeleteTicketMacroCommand(id), HttpContext.RequestAborted)).ToActionResult();
    }
}
