using Discovery.Core.Cqrs.Logs.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/logs")]
public class LogsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? agentId = null,
        [FromQuery] string? siteId = null,
        [FromQuery] string? clientId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListLogsQuery(agentId, siteId, clientId, cursor, limit));
        return result.ToActionResult();
    }
}
