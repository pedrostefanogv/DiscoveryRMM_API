using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote.Audit;

/// <summary>
/// Serviço de auditoria para sessões remotas.
/// Registra eventos de início, fim, renovação e erros.
/// </summary>
public class RemoteSessionAuditService
{
    private readonly IRemoteSessionRepository _sessionRepo;
    private readonly IRemoteSessionAuditRepository _auditRepo;
    private readonly ILogger<RemoteSessionAuditService> _logger;

    public RemoteSessionAuditService(
        IRemoteSessionRepository sessionRepo,
        IRemoteSessionAuditRepository auditRepo,
        ILogger<RemoteSessionAuditService> logger)
    {
        _sessionRepo = sessionRepo;
        _auditRepo = auditRepo;
        _logger = logger;
    }

    /// <summary>Registra um evento de auditoria.</summary>
    public async Task RecordAsync(
        Guid sessionId,
        string eventType,
        string? actorUserId = null,
        string? ipAddress = null,
        string? details = null,
        CancellationToken ct = default)
    {
        var audit = new RemoteSessionAudit
        {
            Id = Guid.NewGuid(),
            RemoteSessionId = sessionId,
            EventType = eventType,
            ActorUserId = actorUserId,
            IpAddress = ipAddress,
            Details = details,
            OccurredAt = DateTime.UtcNow,
        };

        _logger.LogInformation(
            "[RemoteAudit] Session={SessionId} Event={EventType} Actor={Actor} IP={IP}",
            sessionId, eventType, actorUserId ?? "system", ipAddress ?? "—");

        // Persiste o evento: antes o método era apenas log e a auditoria de
        // expiração do sweeper era perdida (nada gravado em remote_session_audits).
        await _auditRepo.AddAsync(audit, ct);
    }

    /// <summary>Registra fechamento de sessão expirada.</summary>
    public async Task RecordExpirationAsync(RemoteSession session, CancellationToken ct = default)
    {
        if (session.Status == "expired") return;

        await RecordAsync(
            session.Id,
            "expired",
            details: $"{{\"startedAt\":\"{session.StartedAt:O}\",\"closedAt\":\"{DateTime.UtcNow:O}\",\"framesSent\":{session.FramesSent},\"bytesSent\":{session.BytesSent}}}",
            ct: ct);

        _logger.LogWarning("Remote session {SessionId} expired — was active since {StartedAt}",
            session.Id, session.StartedAt);
    }
}
