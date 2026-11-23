using Discovery.Api.Filters;
using Discovery.Core.Cqrs.CustomFieldTemplates;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Catálogo de modelos de campos personalizados (pré-configurados).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/custom-field-templates")]
public class CustomFieldTemplatesController(IMediator mediator, IScopeContext scopeContext) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] bool includeGlobal = true,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool allScopes = false)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (allScopes && !access.HasGlobalAccess)
            allScopes = false;

        if (clientId.HasValue && !access.HasGlobalAccess && !access.AllowedClientIds.Contains(clientId.Value))
            return Forbid();

        return (await mediator.Send(
            new ListCustomFieldTemplatesQuery(clientId, departmentId, includeGlobal, includeInactive, allScopes),
            HttpContext.RequestAborted)).ToActionResult();
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Create([FromBody] CreateCustomFieldTemplateCommand cmd)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!access.HasGlobalAccess && (!cmd.ClientId.HasValue || !access.AllowedClientIds.Contains(cmd.ClientId.Value)))
            return Forbid();

        return (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCustomFieldTemplateCommand cmd)
        => (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(Guid id)
        => (await mediator.Send(new DeleteCustomFieldTemplateCommand(id), HttpContext.RequestAborted)).ToActionResult();
}
