using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints do AGENTE para o fluxo de consentimento de acesso remoto.
/// A autenticação é feita pelo middleware de agent-auth (mesmo padrão do
/// suporte/chat): o controller só confia no AgentId colocado no HttpContext.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/agent-auth/me/remote-sessions")]
[AllowAnonymous]
public class AgentRemoteSessionsController(
    IRemoteSessionManager sessionManager,
    ILogger<AgentRemoteSessionsController> logger) : ControllerBase
{
    private bool TryGetAgentId(out Guid agentId)
    {
        if (HttpContext.Items["AgentId"] is Guid id)
        {
            agentId = id;
            return true;
        }

        agentId = Guid.Empty;
        return false;
    }

    /// <summary>
    /// Decisão do usuário local (manual ou timeout). Idempotente: retries do
    /// agente devolvem 200 quando a decisão já é a mesma; uma decisão oposta à
    /// já registrada (ex.: sweeper negou antes) devolve 409 e o agente NÃO inicia.
    /// </summary>
    [HttpPost("{sessionId:guid}/consent")]
    public async Task<IActionResult> ReportConsent(
        Guid sessionId,
        [FromBody] AgentRemoteConsentDecision? request,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        if (request is null)
            return BadRequest(new { error = "Body obrigatório." });

        var raw = await sessionManager.GetRawSessionAsync(sessionId, ct);
        if (raw is null)
            return NotFound(new { error = "Remote session not found." });
        if (raw.AgentId != agentId)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Session does not belong to this agent." });

        var outcome = string.IsNullOrWhiteSpace(request.Outcome)
            ? (request.Accepted ? "granted" : "denied")
            : request.Outcome.Trim();
        if (outcome.Length > 64) outcome = outcome[..64];

        if (raw.Status != RemoteSessionStatus.PendingConsent)
        {
            var alreadyMatches =
                (request.Accepted && raw.Status == RemoteSessionStatus.Active)
                || (!request.Accepted && raw.Status == RemoteSessionStatus.Denied
                    && string.Equals(raw.ConsentOutcome, outcome, StringComparison.OrdinalIgnoreCase));
            if (alreadyMatches)
            {
                return Ok(BuildPayload(raw));
            }

            logger.LogWarning(
                "[RemoteConsent] Decisão tardia para sessão {SessionId}: status atual {Status}, decisão recebida accepted={Accepted}",
                sessionId, raw.Status, request.Accepted);
            return Conflict(new
            {
                error = "Session is no longer waiting for consent.",
                reason = "consent_already_decided",
                session = BuildPayload(raw)
            });
        }

        // Aceite muito depois do deadline não pode "ressuscitar" a sessão: o
        // sweeper pode estar prestes a negá-la. 10s cobrem a latência do report.
        if (request.Accepted
            && raw.ConsentDeadlineAt.HasValue
            && raw.ConsentDeadlineAt.Value.AddSeconds(10) < DateTime.UtcNow)
        {
            logger.LogWarning(
                "[RemoteConsent] Aceite tardio rejeitado para sessão {SessionId}: deadline {Deadline}",
                sessionId, raw.ConsentDeadlineAt);
            return Conflict(new
            {
                error = "Consent deadline exceeded.",
                reason = "consent_deadline_exceeded",
                session = BuildPayload(raw)
            });
        }

        var updated = await sessionManager.ApplyConsentDecisionAsync(
            sessionId, request.Accepted, outcome, request.Reason, request.PromptBackend, ct, request.Auto);

        return Ok(BuildPayload(updated));
    }

    /// <summary>Encerramento iniciado pelo agente (botão Desconectar do banner).</summary>
    [HttpPost("{sessionId:guid}/stop")]
    public async Task<IActionResult> StopFromAgent(
        Guid sessionId,
        [FromBody] AgentRemoteStopRequest? request,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var raw = await sessionManager.GetRawSessionAsync(sessionId, ct);
        if (raw is null)
            return NotFound(new { error = "Remote session not found." });
        if (raw.AgentId != agentId)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Session does not belong to this agent." });

        if (RemoteSessionStatus.IsOpen(raw.Status))
        {
            var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "banner-disconnect" : request!.Reason!;
            await sessionManager.CloseSessionAsync(sessionId, reason, null, ct);
        }

        return NoContent();
    }

    private static object BuildPayload(Discovery.Core.Entities.RemoteSession session) => new
    {
        sessionId = session.Id,
        status = session.Status,
        consent = new
        {
            required = session.ConsentRequired,
            status = session.Status == RemoteSessionStatus.PendingConsent
                ? "pending"
                : session.ConsentOutcome ?? "not_required",
            deadlineUtc = session.ConsentDeadlineAt,
            decidedAtUtc = session.ConsentDecidedAt,
            outcome = session.ConsentOutcome,
            policySource = session.ConsentPolicySource
        }
    };
}

public record AgentRemoteConsentDecision(
    bool Accepted,
    bool Auto = false,
    string? Outcome = null,
    string? Reason = null,
    string? PromptBackend = null,
    DateTime? DecidedAtUtc = null);

public record AgentRemoteStopRequest(string? Reason = null);
