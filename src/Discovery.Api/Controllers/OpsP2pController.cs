using Discovery.Core.Cqrs.AgentP2p.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ops/p2p")]
public class OpsP2pController(IMediator mediator) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null, [FromQuery] int windowHours = 24, CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetP2pOverviewQuery(scope, tenantId, siteId, agentId, windowHours), ct);
        return result.ToActionResult();
    }

    [HttpGet("timeseries")]
    public async Task<IActionResult> GetTimeseries(
        [FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null, [FromQuery] string metric = "peers", [FromQuery] int windowHours = 24,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetP2pTimeseriesQuery(scope, tenantId, siteId, agentId, metric, windowHours), ct);
        return result.ToActionResult();
    }

    [HttpGet("agents/ranking")]
    public async Task<IActionResult> GetAgentRanking(
        [FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null,
        [FromQuery] int windowHours = 24, [FromQuery] string sortBy = "peers", CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetP2pAgentRankingQuery(scope, tenantId, siteId, windowHours, sortBy), ct);
        return result.ToActionResult();
    }

    [HttpGet("seed-plan")]
    public async Task<IActionResult> GetSeedPlan(
        [FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetP2pSeedPlanQuery(scope, tenantId, siteId), ct);
        return result.ToActionResult();
    }
}
