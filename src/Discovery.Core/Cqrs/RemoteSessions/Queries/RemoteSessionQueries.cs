using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.RemoteSessions.Commands;

namespace Discovery.Core.Cqrs.RemoteSessions.Queries;

public sealed record GetActiveSessionsQuery(Guid AgentId, Guid UserId) : IQuery<Result<IEnumerable<ActiveSessionDto>>>;

public sealed record GetSessionCredentialsQuery(Guid AgentId, Guid SessionId, Guid UserId) : IQuery<Result<SessionCredentialsDto>>;

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

/// <summary>
/// Politica de teto de banda exposta para a UI montar o seletor sem hardcode
/// de valores (os presets vem da configuracao do servidor).
/// </summary>
public sealed record BandwidthPolicyDto(
    string DefaultMode,
    int ManualDefaultKbps,
    IReadOnlyList<BandwidthPresetDto> ManualPresets,
    int AutoMinKbps,
    int AutoMaxKbps,
    int AutoStepKbps,
    int ServerMaxKbps
);

/// <summary>Preset de teto de banda exibido na UI. Kbps = 0 significa sem limite.</summary>
public sealed record BandwidthPresetDto(string Id, string Label, int Kbps);
