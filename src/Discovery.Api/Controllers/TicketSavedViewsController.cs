using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-saved-views")]
public class TicketSavedViewsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = HttpContext.Items["UserId"] as Guid?;
        var result = await mediator.Send(new ListTicketSavedViewsQuery(userId));
        return result.ToActionResult();
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetTicketSavedViewByIdQuery(id));
        return result.ToActionResult();
    }
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketSavedViewRequest req)
    {
        var userId = HttpContext.Items["UserId"] as Guid?;
        var result = await mediator.Send(new CreateTicketSavedViewCommand(req.Name, req.FilterJson, req.IsShared, userId));
        return result.Match<IActionResult>(success: v => CreatedAtAction(nameof(GetById), new { id = v.Id }, v), failure: BadRequest);
    }
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketSavedViewRequest req)
    {
        var result = await mediator.Send(new UpdateTicketSavedViewCommand(id, req.Name, req.FilterJson, req.IsShared));
        return result.ToActionResult();
    }
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) { await mediator.Send(new DeleteTicketSavedViewCommand(id)); return NoContent(); }
    private IActionResult BadRequest(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) });
    private IActionResult NotFound(IReadOnlyList<Discovery.Core.Cqrs.Error> errors) => errors[0].Code == "NotFound" ? NotFound() : BadRequest(errors);
}
public record CreateTicketSavedViewRequest(string Name, string? FilterJson, bool IsShared = false);
public record UpdateTicketSavedViewRequest(string? Name, string? FilterJson, bool? IsShared);
