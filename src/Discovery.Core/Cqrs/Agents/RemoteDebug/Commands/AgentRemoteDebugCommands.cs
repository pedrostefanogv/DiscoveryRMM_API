using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.RemoteDebug.Commands;

public sealed record StartRemoteDebugCommand(Guid AgentId, Guid UserId, string? Payload) : ICommand<Result<RemoteDebugResponseDto>>;
public sealed record StopRemoteDebugCommand(Guid AgentId, Guid SessionId, Guid UserId) : ICommand<Result<VoidResult>>;

/// <summary>Renova o TTL da sessao de debug remoto (keepalive do viewer).</summary>
public sealed record RenewRemoteDebugCommand(Guid AgentId, Guid SessionId, Guid UserId) : ICommand<Result<RemoteDebugRenewalDto>>;

/// <summary>Troca o nivel de log da sessao viva, sem reiniciar.</summary>
public sealed record SetRemoteDebugLogLevelCommand(Guid AgentId, Guid SessionId, Guid UserId, string? LogLevel) : ICommand<Result<RemoteDebugLevelDto>>;

public sealed record RemoteDebugResponseDto(
    Guid SessionId,
    [property: JsonPropertyName("natsTenantSubject")] string Subject,
    int Port,
    string Status,
    Guid AgentId,
    DateTime ExpiresAtUtc,
    string? NatsWssUrl = null,
    DateTime? MaxExpiresAtUtc = null,
    int PingIntervalSeconds = 5,
    int MissedPingsBeforeClose = 3,
    int InitialGraceSeconds = 60,
    int KeepAliveSeconds = 60,
    [property: JsonPropertyName("natsControlSubject")] string? ControlSubject = null);

public sealed record RemoteDebugRenewalDto(
    Guid SessionId,
    DateTime ExpiresAtUtc,
    DateTime? MaxExpiresAtUtc,
    bool SessionActive);

public sealed record RemoteDebugLevelDto(
    Guid SessionId,
    string LogLevel,
    DateTime AppliedAtUtc);
