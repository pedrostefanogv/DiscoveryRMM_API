using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Discovery.Api;
using Discovery.Api.Filters;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-saved-views")]
public class TicketSavedViewsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> List()
    {
        var userId = HttpContext.Items["UserId"] as Guid?;
        var result = await mediator.Send(new ListTicketSavedViewsQuery(userId));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var view = await db.TicketSavedViews.AsNoTracking()
            .Where(v => v.Id == id)
            .Select(v => new { v.UserId, v.IsShared })
            .FirstOrDefaultAsync();
        if (view is null)
            return NotFound(new { error = "Saved view not found." });

        var currentUserId = HttpContext.Items["UserId"] as Guid?;
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess && !view.IsShared && (!currentUserId.HasValue || view.UserId != currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a esta visualização salva." });

        var result = await mediator.Send(new GetTicketSavedViewByIdQuery(id));
        return result.ToActionResult();
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Create([FromBody] CreateTicketSavedViewRequest req)
    {
        var userId = HttpContext.Items["UserId"] as Guid?;
        var result = await mediator.Send(new CreateTicketSavedViewCommand(req.Name, req.FilterJson, req.IsShared, userId));
        return result.Match<IActionResult>(success: v => CreatedAtAction(nameof(GetById), new { id = v.Id }, v), failure: BadRequest);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateTicketSavedViewRequest req,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var view = await db.TicketSavedViews.AsNoTracking()
            .Where(v => v.Id == id)
            .Select(v => new { v.UserId })
            .FirstOrDefaultAsync();
        if (view is null)
            return NotFound(new { error = "Saved view not found." });

        var currentUserId = HttpContext.Items["UserId"] as Guid?;
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess && (!currentUserId.HasValue || view.UserId != currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas o proprietário pode editar esta visualização salva." });

        var result = await mediator.Send(new UpdateTicketSavedViewCommand(id, req.Name, req.FilterJson, req.IsShared));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var view = await db.TicketSavedViews.AsNoTracking()
            .Where(v => v.Id == id)
            .Select(v => new { v.UserId })
            .FirstOrDefaultAsync();
        if (view is null)
            return NotFound(new { error = "Saved view not found." });

        var currentUserId = HttpContext.Items["UserId"] as Guid?;
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess && (!currentUserId.HasValue || view.UserId != currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas o proprietário pode excluir esta visualização salva." });

        await mediator.Send(new DeleteTicketSavedViewCommand(id));
        return NoContent();
    }
    private IActionResult BadRequest(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) });
    private IActionResult NotFound(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => errors[0].Code == "NotFound" ? NotFound() : BadRequest(errors);
}
public record CreateTicketSavedViewRequest(string Name, string? FilterJson, bool IsShared = false);
public record UpdateTicketSavedViewRequest(string? Name, string? FilterJson, bool? IsShared);
