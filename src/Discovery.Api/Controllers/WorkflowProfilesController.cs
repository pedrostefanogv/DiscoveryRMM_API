using Discovery.Api.Filters;
using Discovery.Core.Cqrs.WorkflowProfiles.Commands;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Cqrs.WorkflowProfiles.Queries;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/workflow-profiles")]
public class WorkflowProfilesController(IMediator mediator) : ControllerBase
{
    private async Task<(IActionResult? denied, WorkflowProfileDto? dto)> CheckProfileAccessAsync(
        Guid id,
        ActionType action,
        IScopeContext scopeContext)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, action);
        var result = await mediator.Send(new GetWorkflowProfileByIdQuery(id));
        if (result.IsFailure)
        {
            return (result.Match<IActionResult>(
                success: Ok,
                failure: errors => errors[0].Code == "NotFound"
                    ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                    : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })), null);
        }

        var dto = result.Value!;
        if (!access.HasGlobalAccess)
        {
            if (dto.ClientId is null)
            {
                if (action != ActionType.View)
                    return (StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas administradores globais podem alterar perfis globais." }), null);
            }
            else if (!access.AllowedClientIds.Contains(dto.ClientId.Value))
            {
                return (StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este perfil de workflow." }), null);
            }
        }

        return (null, dto);
    }

    [HttpGet]
    [RequirePermission(ResourceType.Workflow, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool includeGlobal = true)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, ActionType.View);
        if (!access.HasGlobalAccess && clientId.HasValue && !access.AllowedClientIds.Contains(clientId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

        var result = await mediator.Send(new ListWorkflowProfilesQuery(clientId, includeGlobal));
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        return Ok(result.Value!.Where(p => p.ClientId is null || access.AllowedClientIds.Contains(p.ClientId.Value)).ToList());
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(Guid id, [FromServices] IScopeContext scopeContext)
    {
        var (denied, dto) = await CheckProfileAccessAsync(id, ActionType.View, scopeContext);
        return denied ?? Ok(dto);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] CreateWorkflowProfileCommand cmd,
        [FromServices] IScopeContext scopeContext)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Workflow, ActionType.Edit);
        if (!access.HasGlobalAccess && (!cmd.ClientId.HasValue || !access.AllowedClientIds.Contains(cmd.ClientId.Value)))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso para criar perfil de workflow neste escopo." });

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateWorkflowProfileCommand cmd,
        [FromServices] IScopeContext scopeContext)
    {
        var (denied, _) = await CheckProfileAccessAsync(id, ActionType.Edit, scopeContext);
        if (denied is not null)
            return denied;

        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Workflow, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(Guid id, [FromServices] IScopeContext scopeContext)
    {
        var (denied, _) = await CheckProfileAccessAsync(id, ActionType.Edit, scopeContext);
        if (denied is not null)
            return denied;

        var result = await mediator.Send(new DeleteWorkflowProfileCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
