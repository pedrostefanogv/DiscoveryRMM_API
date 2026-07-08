using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ops/p2p")]
public class OpsP2pController(IP2pService p2pService) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] int windowHours = 24,
        CancellationToken ct = default)
    {
        var window = TimeSpan.FromHours(windowHours);
        var result = await p2pService.GetOverviewAsync(scope, tenantId, siteId, agentId, window, ct);
        return Ok(result);
    }

    [HttpGet("timeseries")]
    public async Task<IActionResult> GetTimeseries(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] string metric = "peers",
        [FromQuery] int windowHours = 24,
        CancellationToken ct = default)
    {
        var to = DateTime.UtcNow;
        var from = to.AddHours(-windowHours);
        var interval = TimeSpan.FromMinutes(Math.Max(5, windowHours * 60 / 50));
        var result = await p2pService.GetTimeseriesAsync(scope, tenantId, siteId, agentId, metric, from, to, interval, ct);
        return Ok(result);
    }

    [HttpGet("agents/ranking")]
    public async Task<IActionResult> GetAgentRanking(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] int windowHours = 24,
        [FromQuery] string sortBy = "peers",
        CancellationToken ct = default)
    {
        var window = TimeSpan.FromHours(windowHours);
        var result = await p2pService.GetAgentRankingAsync(scope, tenantId, siteId, window, sortBy, ct);
        return Ok(result);
    }

    [HttpGet("seed-plan")]
    public async Task<IActionResult> GetSeedPlan(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        CancellationToken ct = default)
    {
        var result = await p2pService.GetSeedPlanStatusAsync(scope, tenantId, siteId, ct);
        return Ok(result);
    }
}
