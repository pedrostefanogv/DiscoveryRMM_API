using Discovery.Api.Filters;
using Discovery.Core.Cqrs.CustomFieldTemplates;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/custom-field-templates")]
public class CustomFieldTemplatesController(IMediator mediator) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] bool includeGlobal = true,
        [FromQuery] bool includeInactive = false)
        => (await mediator.Send(
            new ListCustomFieldTemplatesQuery(clientId, departmentId, includeGlobal, includeInactive),
            HttpContext.RequestAborted)).ToActionResult();

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateCustomFieldTemplateCommand cmd)
        => (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCustomFieldTemplateCommand cmd)
        => (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id)
        => (await mediator.Send(new DeleteCustomFieldTemplateCommand(id), HttpContext.RequestAborted)).ToActionResult();
}
