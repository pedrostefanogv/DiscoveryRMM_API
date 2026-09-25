namespace Discovery.Api.Services;

public static class RemoteDebugTransportNames
{
    public const string Nats = "nats";
    public const string NatsWs = "nats_ws";
}

public interface IRemoteDebugSessionManager
{
    RemoteDebugSessionState StartSession(
        Guid agentId,
        Guid userId,
        Guid clientId,
        Guid siteId,
        string? logLevel,
        int? ttlMinutes,
        string? preferredTransport = null);

    bool TryGetSessionForUser(Guid sessionId, Guid userId, out RemoteDebugSessionState? session);

    bool TryGetSessionForAgent(Guid sessionId, Guid agentId, out RemoteDebugSessionState? session);

    bool TryGetSession(Guid sessionId, out RemoteDebugSessionState? session);

    bool CloseSession(Guid sessionId, string reason, Guid? closedByUserId = null);

    /// <summary>
    /// Renova o TTL da sessao (keepalive do viewer). Retorna false quando a
    /// sessao nao existe/esta fechada ou quando o teto total foi atingido
    /// (nesse caso a sessao e fechada com "max-duration").
    /// </summary>
    bool TryRenewSession(Guid sessionId, Guid userId, out RemoteDebugSessionState? session);

    /// <summary>
    /// Aplica um novo nivel de log na sessao viva, sem reiniciar (a entrega ao
    /// agente acontece pelo canal de controle). Retorna false se a sessao nao
    /// existe/nao pertence ao usuario.
    /// </summary>
    bool TrySetLogLevel(Guid sessionId, Guid userId, string? logLevel, out RemoteDebugSessionState? session);

    /// <summary>Registra atividade (compatibilidade com o endpoint de credenciais).</summary>
    void Touch(Guid sessionId);

    long NextSequence(Guid sessionId);

    int CleanupExpiredSessions();
}

public sealed class RemoteDebugSessionState
{
    public Guid SessionId { get; init; }
    public Guid AgentId { get; init; }
    public Guid OwnerUserId { get; init; }
    public Guid ClientId { get; init; }
    public Guid SiteId { get; init; }
    public string LogLevel { get; set; } = "info";
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
    /// <summary>Ultimo keepalive HTTP recebido do viewer.</summary>
    public DateTime LastKeepAliveAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    /// <summary>Teto absoluto da sessao (StartedAtUtc + duracao configurada na instalacao).</summary>
    public DateTime MaxExpiresAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public long SequenceCounter;
    public string PreferredTransport { get; init; } = RemoteDebugTransportNames.Nats;
    public string NatsSubject { get; init; } = string.Empty;
    /// <summary>Subject unico de controle (ping/pong/setLevel).</summary>
    public string NatsControlSubject { get; init; } = string.Empty;

    public bool IsClosed => EndedAtUtc.HasValue;
}
