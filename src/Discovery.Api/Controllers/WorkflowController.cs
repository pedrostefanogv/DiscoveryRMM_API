using Discovery.Api.Filters;
using Discovery.Core.Cqrs.WorkflowState.Commands;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Cqrs.WorkflowState.Queries;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/workflow")]
public class WorkflowController(IMediator mediator) : ControllerBase
{
    private async Task<IActionResult?> EnsureClientWriteAccessAsync(Guid? clientId, IScopeContext scopeContext)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, ActionType.Edit);
        if (access.HasGlobalAccess)
            return null;

        if (!clientId.HasValue || !access.AllowedClientIds.Contains(clientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso para gerenciar workflow neste escopo." });

        return null;
    }

    [HttpGet("states")]
    [RequirePermission(ResourceType.Workflow, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetStates(
        [FromServices] IScopeContext scopeContext,
        [FromQuery] Guid? clientId = null)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, ActionType.View);
        if (!access.HasGlobalAccess && clientId.HasValue && !access.AllowedClientIds.Contains(clientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

        var result = await mediator.Send(new ListWorkflowStatesQuery(clientId));
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        return Ok(result.Value!.Where(s => s.ClientId is null || access.AllowedClientIds.Contains(s.ClientId.Value)).ToList());
    }

    [HttpGet("states/{id:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetStateById(Guid id, [FromServices] IScopeContext scopeContext)
    {
        var result = await mediator.Send(new GetWorkflowStateByIdQuery(id));
        if (result.IsSuccess)
        {
            var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, ActionType.View);
            if (!access.HasGlobalAccess && result.Value!.ClientId.HasValue && !access.AllowedClientIds.Contains(result.Value.ClientId.Value))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este estado de workflow." });
        }

        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("states")]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateState(
        [FromBody] CreateWorkflowStateCommand cmd,
        [FromServices] IScopeContext scopeContext)
    {
        if (await EnsureClientWriteAccessAsync(cmd.ClientId, scopeContext) is { } denied)
            return denied;

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetStateById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("states/{id:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> UpdateState(
        Guid id,
        [FromBody] UpdateWorkflowStateCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var state = await db.WorkflowStates.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.ClientId }).FirstOrDefaultAsync();
        if (state is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Estado não encontrado." } } });

        if (await EnsureClientWriteAccessAsync(state.ClientId, scopeContext) is { } denied)
            return denied;

        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("states/{id:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> DeleteState(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var state = await db.WorkflowStates.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.ClientId }).FirstOrDefaultAsync();
        if (state is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Estado não encontrado." } } });

        if (await EnsureClientWriteAccessAsync(state.ClientId, scopeContext) is { } denied)
            return denied;

        var result = await mediator.Send(new DeleteWorkflowStateCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("transitions")]
    [RequirePermission(ResourceType.Workflow, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetTransitions(
        [FromServices] IScopeContext scopeContext,
        [FromQuery] Guid? clientId = null)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, ActionType.View);
        if (!access.HasGlobalAccess && clientId.HasValue && !access.AllowedClientIds.Contains(clientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

        var result = await mediator.Send(new ListWorkflowTransitionsQuery(clientId));
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        return Ok(result.Value!.Where(t => t.ClientId is null || access.AllowedClientIds.Contains(t.ClientId.Value)).ToList());
    }

    /// <summary>
    /// Transições que partem de um estado. Usado pela console para sugerir os
    /// destinos válidos a partir da origem escolhida.
    /// </summary>
    [HttpGet("transitions/from/{fromStateId:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetTransitionsFrom(
        Guid fromStateId,
        [FromServices] IScopeContext scopeContext,
        [FromQuery] Guid? clientId = null)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, ActionType.View);
        if (!access.HasGlobalAccess && clientId.HasValue && !access.AllowedClientIds.Contains(clientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

        var transitions = await mediator.Send(new ListWorkflowTransitionsQuery(clientId), CancellationToken.None);
        var from = transitions.IsSuccess
            ? transitions.Value!
                .Where(t => t.FromStateId == fromStateId
                            && (access.HasGlobalAccess || t.ClientId is null || access.AllowedClientIds.Contains(t.ClientId.Value)))
                .ToList()
            : [];
        return Ok(from);
    }

    [HttpPost("transitions")]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateTransition(
        [FromBody] CreateWorkflowTransitionCommand cmd,
        [FromServices] IScopeContext scopeContext)
    {
        if (await EnsureClientWriteAccessAsync(cmd.ClientId, scopeContext) is { } denied)
            return denied;

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("transitions/{id:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> DeleteTransition(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        var transition = await db.WorkflowTransitions.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.ClientId }).FirstOrDefaultAsync();
        if (transition is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Transição não encontrada." } } });

        if (await EnsureClientWriteAccessAsync(transition.ClientId, scopeContext) is { } denied)
            return denied;

        var result = await mediator.Send(new DeleteWorkflowTransitionCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
