using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Realtime.Queries;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/realtime")]
public class RealtimeController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> GetStatus()
    {
        var result = await mediator.Send(new GetRealtimeStatusQuery());
        return result.ToActionResult();
    }

    [HttpGet("stats")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> GetStats()
    {
        var result = await mediator.Send(new GetRealtimeStatsQuery());
        return result.ToActionResult();
    }
}
