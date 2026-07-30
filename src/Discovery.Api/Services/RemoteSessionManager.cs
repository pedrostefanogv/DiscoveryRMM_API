using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
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

    public RemoteSessionManager(
        IRemoteSessionRepository repo,
        IRemoteSessionAuditRepository auditRepo,
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteSessionManager> logger)
    {
        _repo = repo;
        _auditRepo = auditRepo;
        _options = options.Value;
        _logger = logger;
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
        CancellationToken ct = default)
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
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UserId = userId,
            TenantId = tenantId,
            SiteId = siteId,
            Kind = kind,
            Transport = transport,
            QualityProfile = quality,
            Codec = codec,
            Status = "active",
            NatsSubject = natsSubject,
            StartedAt = now,
            ExpiresAt = now.AddMinutes(_options.DefaultTtlMinutes)
        };

        var created = await _repo.CreateAsync(session, ct);

        await AuditAsync(created.Id, "started", $"{{\"kind\":\"{kind}\",\"transport\":\"{transport}\",\"quality\":\"{quality}\",\"codec\":\"{codec}\"}}", userId.ToString(), null, ct);

        _logger.LogInformation("Remote session {SessionId} started for agent {AgentId} by user {UserId}",
            created.Id, agentId, userId);

        return created;
    }

    public async Task<RemoteSession> RenewSessionAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (session.Status != "active")
            throw new InvalidOperationException($"Session {sessionId} is not active (status: {session.Status}).");

        if (session.UserId != userId)
            throw new UnauthorizedAccessException($"User {userId} is not the owner of session {sessionId}.");

        // M2: cap de duração máxima total
        if (_options.MaxSessionDurationMinutes > 0)
        {
            var maxExpiry = session.StartedAt.AddMinutes(_options.MaxSessionDurationMinutes);
            if (DateTime.UtcNow >= maxExpiry)
                throw new InvalidOperationException($"Session {sessionId} has reached maximum duration of {_options.MaxSessionDurationMinutes} minutes.");
        }

        session.ExpiresAt = DateTime.UtcNow.AddMinutes(_options.DefaultTtlMinutes);
        var updated = await _repo.UpdateAsync(session, ct);

        await AuditAsync(sessionId, "renewed", $"{{\"newExpiresAt\":\"{updated.ExpiresAt:O}\"}}", userId.ToString(), null, ct);

        return updated;
    }

    public async Task<RemoteSession> CloseSessionAsync(Guid sessionId, string reason, Guid? actorUserId = null, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (session.Status != "active")
            return session; // já fechada, idempotente

        var now = DateTime.UtcNow;
        session.Status = "closed";
        session.ClosedAt = now;
        session.DurationSeconds = (int)(now - session.StartedAt).TotalSeconds;

        var updated = await _repo.UpdateAsync(session, ct);

        var actor = actorUserId?.ToString() ?? "system";
        await AuditAsync(sessionId, "closed", $"{{\"reason\":\"{reason}\",\"durationSec\":{updated.DurationSeconds}}}", actor, null, ct);

        _logger.LogInformation("Remote session {SessionId} closed: {Reason}", sessionId, reason);

        return updated;
    }

    public async Task<RemoteSession?> GetActiveForUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAsync(sessionId, ct);
        if (session is null)
        {
            _logger.LogWarning(
                "[RemoteSession] GetActiveForUserAsync: sessão {SessionId} não encontrada no banco", sessionId);
            return null;
        }

        if (session.Status != "active")
        {
            _logger.LogWarning(
                "[RemoteSession] GetActiveForUserAsync: sessão {SessionId} não está ativa. Status={Status}, UserId={SessionUserId}",
                sessionId, session.Status, session.UserId);
            return null;
        }

        if (session.UserId == Guid.Empty)
        {
            _logger.LogError(
                "[RemoteSession] GetActiveForUserAsync: sessão {SessionId} é ÓRFÃ (UserId=Guid.Empty). " +
                "Provável bug na criação da sessão. AgentId={AgentId}, StartedAt={StartedAt}",
                sessionId, session.AgentId, session.StartedAt);
            return null;
        }

        if (session.UserId != userId)
        {
            _logger.LogWarning(
                "[RemoteSession] GetActiveForUserAsync: UserId mismatch. SessionUserId={SessionUserId}, RequestUserId={RequestUserId}, SessionId={SessionId}",
                session.UserId, userId, sessionId);
            return null;
        }

        return session;
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
            $"{{\"quality\":\"{quality}\",\"codec\":\"{updated.Codec}\"}}",
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
