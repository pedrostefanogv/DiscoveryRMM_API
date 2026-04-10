using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints P2P do agente autenticado.
/// Autenticação: Bearer mdz_... validado pelo AgentAuthMiddleware.
/// </summary>
[ApiController]
[Route("api/agent-auth")]
[AllowAnonymous]
public class AgentP2pController : ControllerBase
{
    private readonly IP2pService _p2p;
    private readonly IAgentRepository _agentRepo;

    public AgentP2pController(IP2pService p2p, IAgentRepository agentRepo)
    {
        _p2p = p2p;
        _agentRepo = agentRepo;
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/agent-auth/me/p2p-seed-plan
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna recomendação de quantos agentes do site devem atuar como seeders.
    /// Cache recomendado no agente: 5 minutos.
    /// </summary>
    [HttpGet("me/p2p-seed-plan")]
    public async Task<IActionResult> GetSeedPlan(CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        try
        {
            var result = await _p2p.GetSeedPlanAsync(agentId, ct);
            return Ok(result);
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "Erro interno calculando plano." });
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // POST /api/agent-auth/me/p2p-telemetry
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recebe métricas de saúde do enxame P2P (~5min).
    /// Retorna 202 Accepted; o agente não precisa aguardar persistência.
    /// </summary>
    [HttpPost("me/p2p-telemetry")]
    public async Task<IActionResult> PostTelemetry(
        [FromBody] P2pTelemetryRequest? request,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        if (request is null)
            return BadRequest(new { error = "Payload inválido ou ausente." });

        // Rate limit por agentId (janela 10min / 5 req)
        var retryAfter = await _p2p.CheckTelemetryRateLimitAsync(agentId, ct);
        if (retryAfter > 0)
        {
            Response.Headers["Retry-After"] = retryAfter.ToString();
            return StatusCode(429, new
            {
                error = "rate limit de telemetria excedido",
                retryAfterSeconds = retryAfter
            });
        }

        var errors = await _p2p.IngestTelemetryAsync(agentId, request, ct);
        if (errors.Count > 0)
        {
            return BadRequest(new
            {
                error = "múltiplos erros de validação",
                details = errors
            });
        }

        return StatusCode(202, new { received = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/agent-auth/me/p2p-distribution-status
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna visibilidade de distribuição por artifact para o dashboard operacional.
    /// AgregaDados recebidos via telemetria dos agentes do site (janela 2h).
    /// </summary>
    [HttpGet("me/p2p-distribution-status")]
    public async Task<IActionResult> GetDistributionStatus(
        [FromQuery] string? artifactId = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        if (limit < 1 || limit > 500)
            return BadRequest(new { error = "limit deve estar entre 1 e 500." });

        if (offset < 0)
            return BadRequest(new { error = "offset deve ser ≥ 0." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var (items, _) = await _p2p.GetDistributionStatusAsync(agentId, artifactId, limit, offset, ct);
        return Ok(items);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helper
    // ─────────────────────────────────────────────────────────────────────

    private bool TryGetAgentId(out Guid agentId)
    {
        agentId = Guid.Empty;
        if (!HttpContext.Items.TryGetValue("AgentId", out var value) || value is not Guid parsed)
            return false;
        agentId = parsed;
        return true;
    }
}
