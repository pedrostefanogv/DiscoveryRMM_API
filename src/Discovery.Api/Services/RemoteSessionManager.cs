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
        CancellationToken ct = default)
    {
        var activeCount = await _repo.CountActiveByAgentAsync(agentId, ct);
        if (activeCount >= _options.MaxConcurrentSessionsPerAgent)
            throw new InvalidOperationException($"Agent already has {activeCount} active sessions (max {_options.MaxConcurrentSessionsPerAgent}).");

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
        if (session is null || session.Status != "active")
            return null;

        if (session.UserId != userId)
            return null;

        return session;
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
