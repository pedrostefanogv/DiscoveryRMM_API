namespace Discovery.Api.Services;

public static class RemoteDebugTransportNames
{
    public const string Nats = "nats";
    public const string SignalR = "signalr";
}

public static class RemoteDebugSignalRMethods
{
    public const string PushLog = "PushRemoteDebugLog";
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
    public DateTime StartedAtUtc { get; init; }
    public DateTime LastActivityAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public long SequenceCounter;
    public string PreferredTransport { get; init; } = RemoteDebugTransportNames.Nats;
    public string FallbackTransport { get; init; } = RemoteDebugTransportNames.SignalR;
    public string NatsSubject { get; init; } = string.Empty;
    public string SignalRMethod { get; init; } = RemoteDebugSignalRMethods.PushLog;

    public bool IsClosed => EndedAtUtc.HasValue;
}
