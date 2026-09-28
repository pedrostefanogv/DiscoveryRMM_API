using Discovery.Core.Cqrs.AgentP2p.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ops/p2p")]
public class OpsP2pController(IMediator mediator) : ControllerBase
{
    /// <summary>Menor janela aceita (1h).</summary>
    internal const int MinWindowHours = 1;

    /// <summary>Maior janela aceita (30 dias) — cobre o seletor do dashboard.</summary>
    internal const int MaxWindowHours = 24 * 30;

    // Valores fora da faixa causavam OverflowException em TimeSpan.FromHours /
    // DateTime.AddHours (ex.: ?windowHours=2000000000 → HTTP 500). O clamp evita
    // o 500 e limita a janela consultada.
    internal static int NormalizeWindowHours(int windowHours) =>
        Math.Clamp(windowHours, MinWindowHours, MaxWindowHours);

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null, [FromQuery] int windowHours = 24, CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetP2pOverviewQuery(scope, tenantId, siteId, agentId, NormalizeWindowHours(windowHours)), ct);
        return result.ToActionResult();
    }

    [HttpGet("timeseries")]
    public async Task<IActionResult> GetTimeseries(
        [FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null, [FromQuery] string metric = "peers", [FromQuery] int windowHours = 24,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetP2pTimeseriesQuery(scope, tenantId, siteId, agentId, metric, NormalizeWindowHours(windowHours)), ct);
        return result.ToActionResult();
    }

    [HttpGet("agents/ranking")]
    public async Task<IActionResult> GetAgentRanking(
        [FromQuery] string scope = "global", [FromQuery] Guid? tenantId = null, [FromQuery] Guid? siteId = null,
        [FromQuery] int windowHours = 24, [FromQuery] string sortBy = "peers", CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetP2pAgentRankingQuery(scope, tenantId, siteId, NormalizeWindowHours(windowHours), sortBy), ct);
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
