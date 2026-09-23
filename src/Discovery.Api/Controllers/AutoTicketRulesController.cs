using Discovery.Core.Cqrs.AutoTicketRules.Commands;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;
using Discovery.Api.Filters;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/auto-ticket-rules")]
public class AutoTicketRulesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] string? scopeLevel = null, [FromQuery] Guid? scopeId = null, [FromQuery] bool? isEnabled = null)
    {
        var result = await mediator.Send(new ListAutoTicketRulesQuery(scopeLevel, scopeId, isEnabled));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetAutoTicketRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateAutoTicketRuleCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutoTicketRuleCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteAutoTicketRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // O frontend chama PATCH /{id}/enable|disable. O handler de update já aplica
    // apenas os campos não nulos, então basta enviar IsEnabled.
    [HttpPatch("{id:guid}/enable")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public Task<IActionResult> Enable(Guid id) => SetEnabledAsync(id, true);

    [HttpPatch("{id:guid}/disable")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public Task<IActionResult> Disable(Guid id) => SetEnabledAsync(id, false);

    private async Task<IActionResult> SetEnabledAsync(Guid id, bool enabled)
    {
        var result = await mediator.Send(new UpdateAutoTicketRuleCommand(
            id, null, enabled, null, null, null, null, string.Empty, null, null, null, null, null));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }
}
