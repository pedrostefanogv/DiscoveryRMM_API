using Meduza.Api.Filters;
using Meduza.Core.DTOs;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

/// <summary>
/// API de operações/dashboard P2P para o frontend.
/// Requer sessão de usuário autenticado.
/// Oferece visão em 4 escopos: global / tenant / site / agent.
/// </summary>
[ApiController]
[Route("api/ops/p2p")]
[RequireUserAuth]
public class OpsP2pController : ControllerBase
{
    private readonly IP2pService _p2p;

    public OpsP2pController(IP2pService p2p) => _p2p = p2p;

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/ops/p2p/overview
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// KPIs / cards de resumo.
    /// scope: global | tenant | site | agent
    /// window: 5m | 1h | 24h | 7d (default: 24h)
    /// </summary>
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] string window = "24h",
        CancellationToken ct = default)
    {
        if (!TryParseWindow(window, out var windowSpan))
            return BadRequest(new { error = "window inválido. Use: 5m, 1h, 24h, 7d." });

        if (!IsValidScope(scope, tenantId, siteId, agentId, out var scopeError))
            return BadRequest(new { error = scopeError });

        var result = await _p2p.GetOverviewAsync(scope, tenantId, siteId, agentId, windowSpan, ct);
        return Ok(result);
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/ops/p2p/timeseries
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Série temporal de uma métrica.
    /// metric: replicationsSucceeded | replicationsFailed | replicationsStarted |
    ///         bytesServed | bytesDownloaded | activeReplications | queuedReplications | chunkedDownloads
    /// interval: 1m | 5m | 15m | 1h (default: 5m)
    /// from/to: ISO8601 (default: últimas 24h)
    /// </summary>
    [HttpGet("timeseries")]
    public async Task<IActionResult> GetTimeseries(
        [FromQuery] string metric = "replicationsSucceeded",
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string interval = "5m",
        CancellationToken ct = default)
    {
        var toUtc = to is not null && DateTime.TryParse(to, out var tp)
            ? tp.ToUniversalTime()
            : DateTime.UtcNow;

        var fromUtc = from is not null && DateTime.TryParse(from, out var fp)
            ? fp.ToUniversalTime()
            : toUtc.AddHours(-24);

        if (!TryParseWindow(interval, out var intervalSpan))
            return BadRequest(new { error = "interval inválido. Use: 1m, 5m, 15m, 1h." });

        var result = await _p2p.GetTimeseriesAsync(scope, tenantId, siteId, agentId, metric, fromUtc, toUtc, intervalSpan, ct);
        return Ok(result);
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/ops/p2p/artifacts/distribution
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lista artifacts com distribuição por peers (janela 2h).
    /// scopeId: tenantId | siteId conforme scope.
    /// </summary>
    [HttpGet("artifacts/distribution")]
    public async Task<IActionResult> GetArtifactDistribution(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string? artifactId = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 500)
            return BadRequest(new { error = "limit deve estar entre 1 e 500." });

        if (offset < 0)
            return BadRequest(new { error = "offset deve ser ≥ 0." });

        var (items, total) = await _p2p.GetArtifactDistributionOpsAsync(scope, tenantId, siteId, artifactId, limit, offset, ct);

        return Ok(new
        {
            total,
            limit,
            offset,
            items
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/ops/p2p/agents/ranking
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ranking de agentes por saúde/performance P2P.
    /// sortBy: healthScore | bytesServed | failureRate | queuePressure
    /// </summary>
    [HttpGet("agents/ranking")]
    public async Task<IActionResult> GetAgentRanking(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string window = "24h",
        [FromQuery] string sortBy = "healthScore",
        CancellationToken ct = default)
    {
        if (!TryParseWindow(window, out var windowSpan))
            return BadRequest(new { error = "window inválido. Use: 5m, 1h, 24h, 7d." });

        var result = await _p2p.GetAgentRankingAsync(scope, tenantId, siteId, windowSpan, sortBy, ct);
        return Ok(result);
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/ops/p2p/seed-plan
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Estado atual dos seed-plans por escopo.
    /// </summary>
    [HttpGet("seed-plan")]
    public async Task<IActionResult> GetSeedPlanStatus(
        [FromQuery] string scope = "global",
        [FromQuery] Guid? tenantId = null,
        [FromQuery] Guid? siteId = null,
        CancellationToken ct = default)
    {
        var result = await _p2p.GetSeedPlanStatusAsync(scope, tenantId, siteId, ct);
        return Ok(result);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static bool TryParseWindow(string window, out TimeSpan span)
    {
        span = window switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => TimeSpan.Zero
        };
        return span != TimeSpan.Zero;
    }

    private static bool IsValidScope(string scope, Guid? tenantId, Guid? siteId, Guid? agentId, out string error)
    {
        error = string.Empty;
        return scope switch
        {
            "global" => true,
            "tenant" when tenantId.HasValue => true,
            "tenant" => (error = "tenantId é obrigatório para scope=tenant") == string.Empty,
            "site" when siteId.HasValue => true,
            "site" => (error = "siteId é obrigatório para scope=site") == string.Empty,
            "agent" when agentId.HasValue => true,
            "agent" => (error = "agentId é obrigatório para scope=agent") == string.Empty,
            _ => (error = $"scope inválido: '{scope}'. Use: global, tenant, site, agent") == string.Empty
        };
    }
}
