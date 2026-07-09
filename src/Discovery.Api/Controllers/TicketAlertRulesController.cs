using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-alert-rules")]
public class TicketAlertRulesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new ListTicketAlertRulesQuery());
        return result.Match<IActionResult>(success: Ok, failure: BadRequest);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetTicketAlertRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: NotFound);
    }

    [HttpGet("by-workflow-state/{workflowStateId:guid}")]
    public async Task<IActionResult> GetByWorkflowState(Guid workflowStateId)
    {
        var result = await mediator.Send(new GetTicketAlertRulesByWorkflowStateQuery(workflowStateId));
        return result.Match<IActionResult>(success: Ok, failure: BadRequest);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketAlertRuleRequest req)
    {
        var result = await mediator.Send(new CreateTicketAlertRuleCommand(req.WorkflowStateId, req.Title, req.Message, req.AlertType, req.TimeoutSeconds, req.ActionsJson, req.DefaultAction, req.Icon, req.ScopePreference, req.IsEnabled));
        return result.Match<IActionResult>(success: r => CreatedAtAction(nameof(GetById), new { id = r.Id }, r), failure: BadRequest);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketAlertRuleRequest req)
    {
        var result = await mediator.Send(new UpdateTicketAlertRuleCommand(id, req.WorkflowStateId, req.Title, req.Message, req.AlertType, req.TimeoutSeconds, req.ActionsJson, req.DefaultAction, req.Icon, req.ScopePreference, req.IsEnabled));
        return result.Match<IActionResult>(success: Ok, failure: NotFound);
    }

    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id)
    {
        var result = await mediator.Send(new ToggleTicketAlertRuleCommand(id));
        return result.Match<IActionResult>(success: Ok, failure: NotFound);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) { await mediator.Send(new DeleteTicketAlertRuleCommand(id)); return NoContent(); }

    private IActionResult BadRequest(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) });
    private IActionResult NotFound(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => errors[0].Code == "NotFound" ? NotFound() : BadRequest(errors);
}

public record CreateTicketAlertRuleRequest(Guid WorkflowStateId, string Title, string Message, Discovery.Core.Enums.PsadtAlertType AlertType = Discovery.Core.Enums.PsadtAlertType.Toast, int? TimeoutSeconds = 15, string? ActionsJson = null, string? DefaultAction = null, string Icon = "info", Discovery.Core.Enums.AlertScopeType ScopePreference = Discovery.Core.Enums.AlertScopeType.Agent, bool IsEnabled = true);
public record UpdateTicketAlertRuleRequest(Guid WorkflowStateId, string Title, string Message, Discovery.Core.Enums.PsadtAlertType AlertType, int? TimeoutSeconds, string? ActionsJson, string? DefaultAction, string Icon, Discovery.Core.Enums.AlertScopeType ScopePreference, bool IsEnabled);
