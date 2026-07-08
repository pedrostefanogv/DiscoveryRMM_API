using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class TicketsController : ControllerBase
{
    private readonly IMediator _mediator;

    public TicketsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [Obsolete("Use GET /page (cursor-based) em vez deste endpoint offset.")]
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] TicketFilterQuery filter)
    {
        var query = new ListTicketsQuery(filter);
        var result = await _mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("page")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetPage([FromQuery] TicketFilterQuery filter)
    {
        var query = new ListTicketsQuery(filter);
        var result = await _mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var query = new GetTicketByIdQuery(id);
        var result = await _mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateTicketCommand command)
    {
        var result = await _mediator.Send(command, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) })
        );
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketCommand command)
    {
        var cmd = command with { Id = id };
        var result = await _mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) })
        );
    }

    [HttpPatch("{id:guid}/workflow-state")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpdateWorkflowState(Guid id, [FromBody] TransitionTicketStateCommand command)
    {
        var cmd = command with { TicketId = id };
        var result = await _mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: r => Ok(new { message = "Workflow state updated", r.TicketId, r.PreviousStateId, r.NewStateId, r.ClosedAt }),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetComments(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        var commentQuery = new GetTicketCommentsQuery(id, cursor, Math.Clamp(limit, 1, 200));
        var result = await _mediator.Send(commentQuery, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpPost("{id:guid}/comments")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddTicketCommentCommand command)
    {
        var cmd = command with { TicketId = id };
        var result = await _mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: r => CreatedAtAction(nameof(GetComments), new { id }, r),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpPost("{id:guid}/merge")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> MergeTickets(Guid id, [FromBody] MergeTicketsCommand command)
    {
        var cmd = command with { TargetTicketId = id };
        var result = await _mediator.Send(cmd, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("{id:guid}/sla")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetSlaStatus(Guid id)
    {
        var query = new GetTicketSlaStatusQuery(id);
        var result = await _mediator.Send(query, HttpContext.RequestAborted);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound()
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }
}
