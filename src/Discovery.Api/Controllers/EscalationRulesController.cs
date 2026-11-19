using Discovery.Api.Filters;
using Discovery.Core.Cqrs.EscalationRules.Commands;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Cqrs.EscalationRules.Queries;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/escalation-rules")]
public class EscalationRulesController(IMediator mediator) : ControllerBase
{
    private async Task<IActionResult?> EnsureProfileAccessAsync(
        Guid workflowProfileId,
        ActionType action,
        IScopeContext scopeContext,
        DiscoveryDbContext db)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Sla, action);
        if (access.HasGlobalAccess)
            return null;

        var profile = await db.WorkflowProfiles.AsNoTracking()
            .Where(p => p.Id == workflowProfileId)
            .Select(p => new { p.ClientId })
            .FirstOrDefaultAsync();
        if (profile is null)
            return NotFound(new { error = "Workflow profile not found." });

        if (profile.ClientId is null)
        {
            return action == ActionType.View
                ? null
                : StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas administradores globais podem alterar regras globais de escalação." });
        }

        if (!access.AllowedClientIds.Contains(profile.ClientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a esta regra de escalação." });

        return null;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Sla, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] Guid? workflowProfileId = null)
    {
        if (workflowProfileId.HasValue)
        {
            if (await EnsureProfileAccessAsync(workflowProfileId.Value, ActionType.View, scopeContext, db) is { } denied)
                return denied;
        }

        var result = await mediator.Send(new ListEscalationRulesQuery(workflowProfileId));
        if (result.IsFailure)
            return result.ToActionResult();

        var access = await scopeContext.GetAccessAsync(ResourceType.Sla, ActionType.View);
        if (access.HasGlobalAccess)
            return Ok(result.Value!);

        var profileIds = result.Value!.Select(r => r.WorkflowProfileId).Distinct().ToList();
        var profileClients = await db.WorkflowProfiles.AsNoTracking()
            .Where(p => profileIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.ClientId);

        var filtered = result.Value!
            .Where(r => !profileClients.TryGetValue(r.WorkflowProfileId, out var cId)
                        || cId is null
                        || access.AllowedClientIds.Contains(cId.Value))
            .ToList();
        return Ok(filtered);
    }

    /// <summary>
    /// Lista regras de escalation pelo perfil de workflow.
    /// Rota semântica alternativa ao query parameter ?workflowProfileId=.
    /// </summary>
    [HttpGet("by-profile/{profileId:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetByProfile(
        Guid profileId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureProfileAccessAsync(profileId, ActionType.View, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(new ListEscalationRulesQuery(profileId));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var result = await mediator.Send(new GetEscalationRuleByIdQuery(id));
        if (result.IsSuccess)
        {
            if (await EnsureProfileAccessAsync(result.Value!.WorkflowProfileId, ActionType.View, scopeContext, db) is { } denied)
                return denied;
        }

        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Sla, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] CreateEscalationRuleCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureProfileAccessAsync(cmd.WorkflowProfileId, ActionType.Edit, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateEscalationRuleCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var existing = await db.TicketEscalationRules.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.WorkflowProfileId })
            .FirstOrDefaultAsync();
        if (existing is null)
            return NotFound(new { error = "Escalation rule not found." });

        if (await EnsureProfileAccessAsync(existing.WorkflowProfileId, ActionType.Edit, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var existing = await db.TicketEscalationRules.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.WorkflowProfileId })
            .FirstOrDefaultAsync();
        if (existing is null)
            return NotFound(new { error = "Escalation rule not found." });

        if (await EnsureProfileAccessAsync(existing.WorkflowProfileId, ActionType.Edit, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(new DeleteEscalationRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
