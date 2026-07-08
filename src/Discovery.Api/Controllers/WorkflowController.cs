using Discovery.Core.Cqrs.WorkflowState.Commands;
using Discovery.Core.Cqrs.WorkflowState.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/workflow")]
public class WorkflowController(IMediator mediator) : ControllerBase
{
    [HttpGet("states")]
    public async Task<IActionResult> GetStates([FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new ListWorkflowStatesQuery(clientId));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("states/{id:guid}")]
    public async Task<IActionResult> GetStateById(Guid id)
    {
        var result = await mediator.Send(new GetWorkflowStateByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("states")]
    public async Task<IActionResult> CreateState([FromBody] CreateWorkflowStateCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetStateById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("states/{id:guid}")]
    public async Task<IActionResult> UpdateState(Guid id, [FromBody] UpdateWorkflowStateCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("states/{id:guid}")]
    public async Task<IActionResult> DeleteState(Guid id)
    {
        var result = await mediator.Send(new DeleteWorkflowStateCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("transitions")]
    public async Task<IActionResult> GetTransitions([FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new ListWorkflowTransitionsQuery(clientId));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("transitions")]
    public async Task<IActionResult> CreateTransition([FromBody] CreateWorkflowTransitionCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("transitions/{id:guid}")]
    public async Task<IActionResult> DeleteTransition(Guid id)
    {
        var result = await mediator.Send(new DeleteWorkflowTransitionCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
