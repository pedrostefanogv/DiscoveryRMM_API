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
        bool force = false,
        CancellationToken ct = default);

    /// <summary>Renova o TTL da sessão.</summary>
    Task<RemoteSession> RenewSessionAsync(Guid sessionId, Guid userId, CancellationToken ct = default);

    /// <summary>Encerra uma sessão com motivo.</summary>
    Task<RemoteSession> CloseSessionAsync(Guid sessionId, string reason, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>Obtém sessão ativa com validação de usuário.</summary>
    Task<RemoteSession?> GetActiveForUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Obtém uma sessão pelo ID SEM validação de usuário, para fins de diagnóstico.
    /// Retorna a sessão independente de status ou owner.
    /// </summary>
    Task<RemoteSession?> GetRawSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Retorna todas as sessões ativas de um agent, independente do usuário.
    /// Usado pelo StartSession com Force=true para matar sessões órfãs.
    /// </summary>
    Task<IReadOnlyList<RemoteSession>> GetActiveSessionsForAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Atualiza o subject NATS de uma sessão após criação (subject canônico depende do sessionId).</summary>
    Task<RemoteSession> SetNatsSubjectAsync(Guid sessionId, string natsSubject, CancellationToken ct = default);

    /// <summary>Atualiza qualidade/codec/FPS da sessão e registra auditoria.</summary>
    Task<RemoteSession> UpdateQualityAsync(Guid sessionId, Core.Enums.QualityProfile quality, Core.Enums.RemoteCodec? codec = null, CancellationToken ct = default);

    /// <summary>Registra um evento de auditoria na sessão.</summary>
    Task AuditAsync(Guid sessionId, string eventType, string? details = null, string? actorUserId = null, string? ipAddress = null, CancellationToken ct = default);
}
