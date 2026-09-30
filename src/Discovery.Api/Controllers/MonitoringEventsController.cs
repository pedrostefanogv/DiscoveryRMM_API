using Discovery.Api.Filters;
using Discovery.Core.Cqrs.MonitoringEvents.Queries;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/monitoring-events")]
public class MonitoringEventsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Logs, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] Guid? agentId = null, [FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListMonitoringEventsQuery(agentId, clientId, siteId, cursor, limit));
        return result.ToActionResult();
    }
}
