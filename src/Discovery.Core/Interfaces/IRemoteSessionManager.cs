using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Gerenciador de sessões remotas (TTL, renovação, auditoria, credenciais NATS/TURN).
/// </summary>
public interface IRemoteSessionManager
{
    /// <summary>Cria uma nova sessão e registra auditoria de início.</summary>
    Task<RemoteSession> CreateSessionAsync(
        Guid agentId,
        Guid userId,
        Guid tenantId,
        Guid siteId,
        Core.Enums.RemoteSessionKind kind,
        Core.Enums.RemoteTransport transport,
        Core.Enums.QualityProfile quality,
        Core.Enums.RemoteCodec codec,
        string natsSubject,
        CancellationToken ct = default);

    /// <summary>Renova o TTL da sessão.</summary>
    Task<RemoteSession> RenewSessionAsync(Guid sessionId, Guid userId, CancellationToken ct = default);

    /// <summary>Encerra uma sessão com motivo.</summary>
    Task<RemoteSession> CloseSessionAsync(Guid sessionId, string reason, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>Obtém sessão ativa com validação de usuário.</summary>
    Task<RemoteSession?> GetActiveForUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default);

    /// <summary>Registra um evento de auditoria na sessão.</summary>
    Task AuditAsync(Guid sessionId, string eventType, string? details = null, string? actorUserId = null, string? ipAddress = null, CancellationToken ct = default);
}
