using Discovery.Core.Cqrs.MonitoringEvents.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/monitoring-events")]
public class MonitoringEventsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? agentId = null, [FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListMonitoringEventsQuery(agentId, clientId, siteId, cursor, limit));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
