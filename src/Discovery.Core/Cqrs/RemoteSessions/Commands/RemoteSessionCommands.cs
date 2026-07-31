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
    bool Force = false,
    string? Shell = "powershell",
    int? TermCols = 120,
    int? TermRows = 40,
    string? RootPath = "C:\\"
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

public sealed record ChangeRemoteSessionQualityCommand(
    Guid AgentId = default,
    Guid SessionId = default,
    Guid UserId = default,
    QualityProfile Quality = QualityProfile.High,
    RemoteCodec? Codec = null,
    int? ImageQuality = null,
    int? MaxFps = null,
    bool Auto = false
) : ICommand<Result<RemoteSessionResponseDto>>;

// ── Terminal Multi-Tab Commands ──

/// <summary>Cria uma nova aba de terminal na sessão (notifica o agent via NATS).</summary>
public sealed record CreateTerminalTabCommand(
    Guid AgentId,
    Guid SessionId,
    Guid UserId,
    string Shell = "powershell",
    int Cols = 120,
    int Rows = 40
) : ICommand<Result<TerminalTabResponseDto>>;

/// <summary>Fecha uma aba de terminal na sessão (notifica o agent via NATS).</summary>
public sealed record CloseTerminalTabCommand(
    Guid AgentId,
    Guid SessionId,
    Guid UserId,
    Guid TabId
) : ICommand<Result<VoidResult>>;

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

public sealed record TerminalTabResponseDto(
    Guid TabId,
    string Shell,
    string NatsSubject,
    int Cols,
    int Rows
);

public sealed record RecordingResponseDto(
    Guid RecordingId,
    Guid SessionId,
    string Status,
    DateTime StartedAtUtc,
    string? StorageUrl = null
);
