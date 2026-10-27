using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Services;

/// <summary>
/// Gerenciador de sessões de acesso remoto nativo.
/// Persiste no banco via IRemoteSessionRepository.
/// </summary>
public sealed class RemoteSessionManager : IRemoteSessionManager
{
    private readonly IRemoteSessionRepository _repo;
    private readonly IRemoteSessionAuditRepository _auditRepo;
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<RemoteSessionManager> _logger;
    private readonly IAgentCommandDispatcher _commandDispatcher;

    public RemoteSessionManager(
        IRemoteSessionRepository repo,
        IRemoteSessionAuditRepository auditRepo,
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteSessionManager> logger,
        IAgentCommandDispatcher commandDispatcher)
    {
        _repo = repo;
        _auditRepo = auditRepo;
        _options = options.Value;
        _logger = logger;
        _commandDispatcher = commandDispatcher;
    }

    public async Task<RemoteSession> CreateSessionAsync(
        Guid agentId,
        Guid userId,
        Guid tenantId,
        Guid siteId,
        RemoteSessionKind kind,
        RemoteTransport transport,
        QualityProfile quality,
        RemoteCodec codec,
        string natsSubject,
        bool force = false,
        CancellationToken ct = default,
        bool consentRequired = false,
        DateTime? consentDeadlineAt = null,
        string? consentPolicySource = null,
        Guid? id = null)
    {
        var activeSessions = (await _repo.GetActiveByAgentAsync(agentId, ct)).ToList();
        var activeCount = activeSessions.Count;

        if (activeCount >= _options.MaxConcurrentSessionsPerAgent)
        {
            if (!force)
                throw new InvalidOperationException($"Agent already has {activeCount} active session(s) (max {_options.MaxConcurrentSessionsPerAgent}). Use force=true to override.");

            // Força o encerramento de todas as sessões ativas antes de criar a nova
            foreach (var existing in activeSessions)
            {
                await CloseSessionAsync(existing.Id, "overridden-by-new-session", userId, ct);
                _logger.LogInformation("Remote session {SessionId} overridden by new session for agent {AgentId}", existing.Id, agentId);
            }
        }

        var userCount = await _repo.CountActiveByUserAsync(userId, ct);
        if (userCount >= _options.MaxConcurrentSessionsPerUser)
            throw new InvalidOperationException($"User already has {userCount} active sessions (max {_options.MaxConcurrentSessionsPerUser}).");

        var now = DateTime.UtcNow;
        var session = new RemoteSession
        {
            Id = id ?? Guid.NewGuid(),
            AgentId = agentId,
            UserId = userId,
            TenantId = tenantId,
            SiteId = siteId,
            Kind = kind,
            Transport = transport,
            QualityProfile = quality,
            Codec = codec,
            Status = consentRequired ? RemoteSessionStatus.PendingConsent : RemoteSessionStatus.Active,
            NatsSubject = natsSubject,
            StartedAt = now,
            ExpiresAt = now.AddMinutes(_options.DefaultTtlMinutes),
            LastActivityAt = now,
            ConsentRequired = consentRequired,
            ConsentDeadlineAt = consentDeadlineAt,
            ConsentPolicySource = consentPolicySource
        };

        var created = await _repo.CreateAsync(session, ct);

        await AuditAsync(created.Id, consentRequired ? "consent_requested" : "started",
            JsonSerializer.Serialize(new { kind = kind.ToString(), transport = transport.ToString(), quality = quality.ToString(), codec = codec.ToString(), consentRequired }),
            userId.ToString(), null, ct);

        _logger.LogInformation("Remote session {SessionId} started for agent {AgentId} by user {UserId}",
            created.Id, agentId, userId);

        return created;
    }

    public async Task<RemoteSession> RenewSessionAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (session.UserId != userId)
            throw new UnauthorizedAccessException($"User {userId} is not the owner of session {sessionId}.");

        // Sessao ja encerrada: idempotente. Devolve o estado para o handler
        // traduzir em sessionActive=false/endReason (placeholder no viewer).
        if (session.Status != "active")
            return session;

        var now = DateTime.UtcNow;

        // M2 + liveness: o novo prazo e agora + TTL, limitado pelo teto absoluto
        // (startedAt + MaxSessionDurationMinutes). Quando o teto ja foi atingido a
        // sessao e encerrada e devolvida com Status != "active": o handler traduz
        // isso em sessionActive=false/endReason e o viewer exibe o placeholder em
        // vez de tentar renovar para sempre.
        if (!SessionLivenessPolicy.TryClampRenewal(
                now,
                session.StartedAt,
                _options.DefaultTtlMinutes,
                _options.MaxSessionDurationMinutes,
                out var clampedExpiresAt))
        {
            session.Status = "expired";
            session.ExpiresAt = clampedExpiresAt;
            session.ClosedAt = now;
            session.DurationSeconds = (int)(now - session.StartedAt).TotalSeconds;

            var expired = await _repo.UpdateAsync(session, ct);
            await AuditAsync(sessionId, "expired", null, userId.ToString(), null, ct);

            _logger.LogInformation(
                "Remote session {SessionId} reached maximum duration of {MaxMinutes} minutes and was expired on renew",
                sessionId, _options.MaxSessionDurationMinutes);

            return expired;
        }

        session.ExpiresAt = clampedExpiresAt;
        session.LastActivityAt = now;
        var updated = await _repo.UpdateAsync(session, ct);

        await AuditAsync(sessionId, "renewed", JsonSerializer.Serialize(new { newExpiresAt = updated.ExpiresAt }), userId.ToString(), null, ct);

        return updated;
    }

    public async Task<RemoteSession> CloseSessionAsync(Guid sessionId, string reason, Guid? actorUserId = null, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (!RemoteSessionStatus.IsOpen(session.Status))
            return session; // já fechada, idempotente

        var now = DateTime.UtcNow;
        session.Status = RemoteSessionStatus.Closed;
        session.ClosedAt = now;
        session.DurationSeconds = (int)(now - session.StartedAt).TotalSeconds;

        var updated = await _repo.UpdateAsync(session, ct);

        var actor = actorUserId?.ToString() ?? "system";
        await AuditAsync(sessionId, "closed", JsonSerializer.Serialize(new { reason, durationSec = updated.DurationSeconds }), actor, null, ct);

        _logger.LogInformation("Remote session {SessionId} closed: {Reason}", sessionId, reason);

        return updated;
    }

    public Task<RemoteSession?> GetActiveForUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
        => GetForUserAsync(sessionId, userId, allowPending: false, ct);

    /// <inheritdoc />
    public async Task<RemoteSession?> GetForUserAsync(
        Guid sessionId, Guid userId, bool allowPending, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct);
        if (session is null)
        {
            _logger.LogWarning(
                "[RemoteSession] GetForUserAsync: sessão {SessionId} não encontrada no banco", sessionId);
            return null;
        }

        var expectedStatus = allowPending
            ? RemoteSessionStatus.IsOpen(session.Status)
            : session.Status == RemoteSessionStatus.Active;
        if (!expectedStatus)
        {
            _logger.LogWarning(
                "[RemoteSession] GetForUserAsync: sessão {SessionId} fora do status esperado. Status={Status}, AllowPending={AllowPending}",
                sessionId, session.Status, allowPending);
            return null;
        }

        if (session.UserId == Guid.Empty)
        {
            _logger.LogError(
                "[RemoteSession] GetForUserAsync: sessão {SessionId} é ÓRFÃ (UserId=Guid.Empty). " +
                "AgentId={AgentId}, StartedAt={StartedAt}",
                sessionId, session.AgentId, session.StartedAt);
            return null;
        }

        if (session.UserId != userId)
        {
            _logger.LogWarning(
                "[RemoteSession] GetForUserAsync: UserId mismatch. SessionUserId={SessionUserId}, RequestUserId={RequestUserId}, SessionId={SessionId}",
                session.UserId, userId, sessionId);
            return null;
        }

        return session;
    }

    /// <inheritdoc />
    public async Task<RemoteSession> ApplyConsentDecisionAsync(
        Guid sessionId,
        bool accepted,
        string outcome,
        string? reason,
        string? promptBackend,
        CancellationToken ct = default,
        bool auto = false)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        // Idempotência: o agente pode repetir o POST (retry) e o sweeper pode ter
        // decidido antes. Devolve o estado atual sem reescrever.
        if (session.Status != RemoteSessionStatus.PendingConsent)
            return session;

        var now = DateTime.UtcNow;
        session.ConsentDecidedAt = now;
        session.ConsentOutcome = outcome;
        session.Status = accepted ? RemoteSessionStatus.Active : RemoteSessionStatus.Denied;

        if (!accepted)
        {
            session.ClosedAt = now;
            session.DurationSeconds = (int)(now - session.StartedAt).TotalSeconds;
        }

        var updated = await _repo.UpdateAsync(session, ct);

        var auditEvent = accepted
            ? (outcome.StartsWith("timeout", StringComparison.OrdinalIgnoreCase)
                ? "consent_timeout_granted"
                : "consent_granted")
            : (outcome.StartsWith("timeout", StringComparison.OrdinalIgnoreCase)
                ? "consent_timeout_denied"
                : outcome.Equals("stopped-by-server", StringComparison.OrdinalIgnoreCase)
                    ? "consent_stopped"
                    : "consent_denied");

        await AuditAsync(
            sessionId,
            auditEvent,
            JsonSerializer.Serialize(new { accepted, auto, outcome, reason, promptBackend }),
            null, null, ct);

        _logger.LogInformation(
            "Remote session {SessionId} consent decision: accepted={Accepted} outcome={Outcome} reason={Reason} backend={Backend}",
            sessionId, accepted, outcome, reason, promptBackend);

        // Além de marcar Denied no servidor, avisa o AGENTE para encerrar o
        // worker/prompt pendente. EXCEÇÃO: "stopped-by-server" vem do
        // cancelamento do próprio console (StopRemoteSessionCommandHandler), que
        // já despacha o RemoteSessionStop — avisar aqui geraria comando duplicado.
        var stoppedByServer = outcome.Equals("stopped-by-server", StringComparison.OrdinalIgnoreCase);
        if (!accepted && !stoppedByServer)
        {
            await TryNotifyAgentStopAsync(session);
        }

        return updated;
    }

    /// <summary>
    /// Avisa o agente para encerrar o worker/prompt da sessão quando o
    /// consentimento é negado. Best-effort: falha aqui não altera a decisão já
    /// persistida (o agente também encerra por conta própria).
    /// </summary>
    private async Task TryNotifyAgentStopAsync(RemoteSession session)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { action = "stop", sessionId = session.Id });
            // Teto curto: o report de consentimento do agente não pode ficar preso no dispatch.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _commandDispatcher.DispatchAsync(new AgentCommand
            {
                AgentId = session.AgentId,
                CommandType = CommandType.RemoteSessionStop,
                Payload = payload,
            }, cts.Token);
            _logger.LogInformation(
                "Remote session {SessionId}: stop enviado ao agent após consentimento negado", session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao enviar stop ao agent para sessão {SessionId} com consentimento negado", session.Id);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteSession?> GetRawSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _repo.GetByIdAsync(sessionId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteSession>> GetActiveSessionsForAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        var sessions = await _repo.GetActiveByAgentAsync(agentId, ct);
        return sessions.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<RemoteSession> SetNatsSubjectAsync(Guid sessionId, string natsSubject, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        session.NatsSubject = natsSubject;
        var updated = await _repo.UpdateAsync(session, ct);

        _logger.LogInformation("Remote session {SessionId} NATS subject updated to {Subject}", sessionId, natsSubject);
        return updated;
    }

    /// <inheritdoc />
    public async Task<RemoteSession> UpdateQualityAsync(Guid sessionId, QualityProfile quality, RemoteCodec? codec = null, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        session.QualityProfile = quality;
        if (codec.HasValue)
            session.Codec = codec.Value;

        var updated = await _repo.UpdateAsync(session, ct);

        await AuditAsync(sessionId, "quality_changed",
            JsonSerializer.Serialize(new { quality = quality.ToString(), codec = updated.Codec.ToString() }),
            null, null, ct);

        _logger.LogInformation("Remote session {SessionId} quality changed to {Quality}/{Codec}",
            sessionId, quality, updated.Codec);

        return updated;
    }

    public async Task AuditAsync(Guid sessionId, string eventType, string? details = null, string? actorUserId = null, string? ipAddress = null, CancellationToken ct = default)
    {
        var audit = new RemoteSessionAudit
        {
            Id = Guid.NewGuid(),
            RemoteSessionId = sessionId,
            EventType = eventType,
            ActorUserId = actorUserId,
            Details = details,
            IpAddress = ipAddress,
            OccurredAt = DateTime.UtcNow
        };

        await _auditRepo.AddAsync(audit, ct);
        _logger.LogDebug("Remote session {SessionId} audit recorded: {EventType}", sessionId, eventType);
    }
}
