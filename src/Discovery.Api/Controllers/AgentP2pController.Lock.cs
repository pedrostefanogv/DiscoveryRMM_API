using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de lock global P2P via Redis.
/// </summary>
public partial class AgentP2pController
{
    // ─────────────────────────────────────────────────────────────────────
    // POST /api/agent-auth/me/p2p/lock
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tenta adquirir lock global para download de artifact.
    /// </summary>
    [HttpPost("me/p2p/lock")]
    public async Task<IActionResult> AcquireLock(
        [FromBody] P2pLockRequest? request,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        if (request is null || request.ArtifactId == Guid.Empty)
            return BadRequest(new { error = "artifactId é obrigatório." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        var clientId = site?.ClientId ?? Guid.Empty;

        var lockService = HttpContext.RequestServices.GetRequiredService<IP2pLockService>();
        TimeSpan? ttl = request.TtlSeconds.HasValue ? TimeSpan.FromSeconds(request.TtlSeconds.Value) : null;
        var result = await lockService.TryAcquireAsync(clientId, request.ArtifactId, ttl, ct);
        var acquired = result.Acquired;
        var holderToken = result.HolderToken;

        if (!acquired)
        {
            return StatusCode(409, new
            {
                acquired = false,
                message = "Lock already held by another group",
                retryAfterSeconds = 60
            });
        }

        var expiresAt = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(5));
        return Ok(new
        {
            acquired = true,
            holderToken,
            expiresAtUtc = expiresAt.ToString("O")
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // DELETE /api/agent-auth/me/p2p/lock/{artifactId}
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Libera o lock global de um artifact.
    /// </summary>
    [HttpDelete("me/p2p/lock/{artifactId:guid}")]
    public async Task<IActionResult> ReleaseLock(
        Guid artifactId,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        var clientId = site?.ClientId ?? Guid.Empty;

        var lockService = HttpContext.RequestServices.GetRequiredService<IP2pLockService>();
        await lockService.ReleaseAsync(clientId, artifactId, ct);

        return Ok(new { released = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/agent-auth/me/p2p/lock/{artifactId}
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifica se existe lock ativo para o artifact.
    /// </summary>
    [HttpGet("me/p2p/lock/{artifactId:guid}")]
    public async Task<IActionResult> CheckLock(
        Guid artifactId,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        var clientId = site?.ClientId ?? Guid.Empty;

        var lockService = HttpContext.RequestServices.GetRequiredService<IP2pLockService>();
        var exists = await lockService.ExistsAsync(clientId, artifactId, ct);

        return Ok(new { artifactId, locked = exists });
    }
}

/// <summary>
/// Request para adquirir lock P2P.
/// </summary>
public class P2pLockRequest
{
    public Guid ArtifactId { get; set; }

    /// <summary>TTL opcional em segundos. Padrão: 300 (5 min).</summary>
    public int? TtlSeconds { get; set; }
}
