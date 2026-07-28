using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.RemoteSessions.Commands;

public sealed record StartRemoteSessionCommand(
    RemoteSessionKind Kind,
    RemoteTransport Transport,
    QualityProfile Quality,
    RemoteCodec Codec,
    Guid AgentId = default,
    Guid UserId = default,
    Guid TenantId = default,
    Guid SiteId = default,
    int DurationMinutes = 30,
    bool Force = false
) : ICommand<Result<RemoteSessionResponseDto>>;

public sealed record StopRemoteSessionCommand(
    Guid AgentId,
    Guid SessionId,
    Guid UserId
) : ICommand<Result<VoidResult>>;

public sealed record RenewRemoteSessionCommand(
    Guid AgentId,
    Guid SessionId,
    Guid UserId
) : ICommand<Result<RemoteSessionResponseDto>>;

public sealed record AckFrameCommand(
    Guid AgentId,
    Guid SessionId,
    long FrameSeq,
    double RttMs,
    double? JitterMs,
    double? EstimatedBandwidthKbps
) : ICommand<Result<VoidResult>>;

public sealed record StartRecordingCommand(
    Guid AgentId,
    Guid SessionId,
    Guid UserId
) : ICommand<Result<RecordingResponseDto>>;

public sealed record StopRecordingCommand(
    Guid AgentId,
    Guid SessionId,
    Guid UserId
) : ICommand<Result<RecordingResponseDto>>;

// ── DTOs ──

public sealed record RemoteSessionResponseDto(
    Guid SessionId,
    [property: JsonPropertyName("natsSubject")] string NatsSubject,
    Guid AgentId,
    string Kind,
    string Transport,
    string QualityProfile,
    string Codec,
    string Status,
    DateTime ExpiresAtUtc,
    DateTime StartedAtUtc,
    string? NatsWssUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TurnCredentialsDto? TurnCredentials = null
);

public sealed record TurnCredentialsDto(
    string[] Urls,
    string Username,
    string Credential,
    int TtlSeconds
);

public sealed record RecordingResponseDto(
    Guid RecordingId,
    Guid SessionId,
    string Status,
    DateTime StartedAtUtc,
    string? StorageUrl = null
);
