using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.RemoteSessions.Queries;

public sealed record GetActiveSessionsQuery(Guid AgentId, Guid UserId) : IQuery<Result<IEnumerable<ActiveSessionDto>>>;

public sealed record GetSessionCredentialsQuery(Guid AgentId, Guid SessionId, Guid UserId) : IQuery<Result<SessionCredentialsDto>>;

public sealed record GetTurnCredentialsQuery(Guid AgentId, Guid SessionId, Guid UserId) : IQuery<Result<TurnCredentialsDto>>;

public sealed record GetRecordingDownloadQuery(Guid AgentId, Guid SessionId, Guid UserId) : IQuery<Result<RecordingDownloadDto>>;

// ── DTOs ──

public sealed record ActiveSessionDto(
    Guid SessionId,
    Guid AgentId,
    Guid UserId,
    string Kind,
    string Transport,
    string QualityProfile,
    string Codec,
    string Status,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc,
    bool RecordingEnabled,
    string? NatsSubject
);

public sealed record SessionCredentialsDto(
    string Jwt,
    string NkeySeed,
    DateTime ExpiresAtUtc,
    string? NatsWssUrl
);

public sealed record RecordingDownloadDto(
    string DownloadUrl,
    string ContainerFormat,
    long Bytes,
    int DurationSec,
    DateTime? ExpiresAt
);
