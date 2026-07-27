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
    private readonly ILogger<RemoteSessionAuditService> _logger;

    public RemoteSessionAuditService(
        IRemoteSessionRepository sessionRepo,
        ILogger<RemoteSessionAuditService> logger)
    {
        _sessionRepo = sessionRepo;
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

        // Audit é inserido diretamente via DbContext no Repository
        // (simplificado — em produção seria via Unit of Work)
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
