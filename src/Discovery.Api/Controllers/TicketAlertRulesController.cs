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
[Route("api/v{version:apiVersion}/ticket-alert-rules")]
public class TicketAlertRulesController(IMediator mediator) : ControllerBase
{
    private async Task<IActionResult?> EnsureWorkflowStateAccessAsync(
        Guid workflowStateId,
        ActionType action,
        IScopeContext scopeContext,
        DiscoveryDbContext db)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, action);
        if (access.HasGlobalAccess)
            return null;

        var state = await db.WorkflowStates.AsNoTracking()
            .Where(s => s.Id == workflowStateId)
            .Select(s => new { s.ClientId })
            .FirstOrDefaultAsync();
        if (state is null)
            return NotFound(new { error = "Workflow state not found." });

        if (state.ClientId is null)
        {
            return action == ActionType.View
                ? null
                : StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas administradores globais podem alterar regras de alerta globais." });
        }

        if (!access.AllowedClientIds.Contains(state.ClientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a esta regra de alerta." });

        return null;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var result = await mediator.Send(new ListTicketAlertRulesQuery());
        if (result.IsFailure)
            return result.ToActionResult();

        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (access.HasGlobalAccess)
            return Ok(result.Value!);

        var stateIds = result.Value!.Select(r => r.WorkflowStateId).Distinct().ToList();
        var stateClients = await db.WorkflowStates.AsNoTracking()
            .Where(s => stateIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.ClientId);

        var filtered = result.Value!
            .Where(r => !stateClients.TryGetValue(r.WorkflowStateId, out var cId)
                        || cId is null
                        || access.AllowedClientIds.Contains(cId.Value))
            .ToList();
        return Ok(filtered);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var result = await mediator.Send(new GetTicketAlertRuleByIdQuery(id));
        if (result.IsSuccess)
        {
            if (await EnsureWorkflowStateAccessAsync(result.Value!.WorkflowStateId, ActionType.View, scopeContext, db) is { } denied)
                return denied;
        }
        return result.ToActionResult();
    }

    [HttpGet("by-workflow-state/{workflowStateId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetByWorkflowState(
        Guid workflowStateId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureWorkflowStateAccessAsync(workflowStateId, ActionType.View, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(new GetTicketAlertRulesByWorkflowStateQuery(workflowStateId));
        return result.ToActionResult();
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTicketAlertRuleRequest req,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureWorkflowStateAccessAsync(req.WorkflowStateId, ActionType.Edit, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(new CreateTicketAlertRuleCommand(req.WorkflowStateId, req.Title, req.Message, req.AlertType, req.TimeoutSeconds, req.ActionsJson, req.DefaultAction, req.Icon, req.ScopePreference, req.IsEnabled));
        return result.Match<IActionResult>(success: r => CreatedAtAction(nameof(GetById), new { id = r.Id }, r), failure: BadRequest);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateTicketAlertRuleRequest req,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var existing = await db.TicketAlertRules.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.WorkflowStateId })
            .FirstOrDefaultAsync();
        if (existing is null)
            return NotFound(new { error = "Ticket alert rule not found." });

        if (await EnsureWorkflowStateAccessAsync(existing.WorkflowStateId, ActionType.Edit, scopeContext, db) is { } deniedExisting)
            return deniedExisting;
        if (await EnsureWorkflowStateAccessAsync(req.WorkflowStateId, ActionType.Edit, scopeContext, db) is { } deniedTarget)
            return deniedTarget;

        var result = await mediator.Send(new UpdateTicketAlertRuleCommand(id, req.WorkflowStateId, req.Title, req.Message, req.AlertType, req.TimeoutSeconds, req.ActionsJson, req.DefaultAction, req.Icon, req.ScopePreference, req.IsEnabled));
        return result.ToActionResult();
    }

    [HttpPatch("{id:guid}/toggle")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Toggle(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var existing = await db.TicketAlertRules.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.WorkflowStateId })
            .FirstOrDefaultAsync();
        if (existing is null)
            return NotFound(new { error = "Ticket alert rule not found." });

        if (await EnsureWorkflowStateAccessAsync(existing.WorkflowStateId, ActionType.Edit, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(new ToggleTicketAlertRuleCommand(id));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var existing = await db.TicketAlertRules.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.WorkflowStateId })
            .FirstOrDefaultAsync();
        if (existing is null)
            return NotFound(new { error = "Ticket alert rule not found." });

        if (await EnsureWorkflowStateAccessAsync(existing.WorkflowStateId, ActionType.Edit, scopeContext, db) is { } denied)
            return denied;

        await mediator.Send(new DeleteTicketAlertRuleCommand(id));
        return NoContent();
    }

    private IActionResult BadRequest(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) });
    private IActionResult NotFound(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => errors[0].Code == "NotFound" ? NotFound() : BadRequest(errors);
}

public record CreateTicketAlertRuleRequest(Guid WorkflowStateId, string Title, string Message, Discovery.Core.Enums.PsadtAlertType AlertType = Discovery.Core.Enums.PsadtAlertType.Toast, int? TimeoutSeconds = 15, string? ActionsJson = null, string? DefaultAction = null, string Icon = "info", Discovery.Core.Enums.AlertScopeType ScopePreference = Discovery.Core.Enums.AlertScopeType.Agent, bool IsEnabled = true);
public record UpdateTicketAlertRuleRequest(Guid WorkflowStateId, string Title, string Message, Discovery.Core.Enums.PsadtAlertType AlertType, int? TimeoutSeconds, string? ActionsJson, string? DefaultAction, string Icon, Discovery.Core.Enums.AlertScopeType ScopePreference, bool IsEnabled);
