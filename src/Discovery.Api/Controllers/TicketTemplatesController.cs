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
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] bool includeGlobal = true,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] bool allClients = false)
        => (await mediator.Send(
            new ListTicketTemplatesQuery(clientId, departmentId, includeGlobal, includeInactive, includeDeleted, allClients),
            HttpContext.RequestAborted)).ToActionResult();

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateTicketTemplateCommand cmd)
        => (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketTemplateCommand cmd)
        => (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();

    /// <summary>
    /// Sem permanent: soft delete (vai para a lixeira, restaurável).
    /// Com permanent=true: exclusão física; sem force=true recusa quando o
    /// template já foi usado por chamados (409 com a contagem).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force = false, [FromQuery] bool permanent = false)
    {
        var result = permanent
            ? await mediator.Send(new PurgeTicketTemplateCommand(id, force), HttpContext.RequestAborted)
            : await mediator.Send(new DeleteTicketTemplateCommand(id, Username), HttpContext.RequestAborted);
        return result.ToActionResult();
    }

    /// <summary>Tira o template da lixeira (volta a aparecer na listagem conforme IsActive).</summary>
    [HttpPost("{id:guid}/restore")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Restore(Guid id)
        => (await mediator.Send(new RestoreTicketTemplateCommand(id), HttpContext.RequestAborted)).ToActionResult();
}
