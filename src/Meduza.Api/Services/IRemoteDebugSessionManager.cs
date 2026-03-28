namespace Meduza.Api.Services;

public interface IRemoteDebugSessionManager
{
    RemoteDebugSessionState StartSession(
        Guid agentId,
        Guid userId,
        Guid clientId,
        Guid siteId,
        string? logLevel,
        string? preferredTransport,
        int? ttlMinutes);

    bool TryGetSessionForUser(Guid sessionId, Guid userId, out RemoteDebugSessionState? session);

    bool TryGetSessionForAgent(Guid sessionId, Guid agentId, out RemoteDebugSessionState? session);

    bool TryGetSession(Guid sessionId, out RemoteDebugSessionState? session);

    bool CloseSession(Guid sessionId, string reason, Guid? closedByUserId = null);

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
    public string PreferredTransport { get; init; } = "signalr";
    public DateTime StartedAtUtc { get; init; }
    public DateTime LastActivityAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public long SequenceCounter;
    public string NatsLegacySubject { get; init; } = string.Empty;
    public string NatsTenantSubject { get; init; } = string.Empty;

    public bool IsClosed => EndedAtUtc.HasValue;
}
