using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Support.Templates;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-templates")]
public class TicketTemplatesController(IMediator mediator) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null, [FromQuery] Guid? departmentId = null, [FromQuery] bool includeGlobal = true)
        => (await mediator.Send(new ListTicketTemplatesQuery(clientId, departmentId, includeGlobal), HttpContext.RequestAborted)).ToActionResult();

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateTicketTemplateCommand cmd)
        => (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketTemplateCommand cmd)
        => (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force = false)
        => (await mediator.Send(new DeleteTicketTemplateCommand(id, force), HttpContext.RequestAborted)).ToActionResult();
}
