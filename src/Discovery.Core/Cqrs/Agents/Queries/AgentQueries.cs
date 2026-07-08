using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Queries;

/// <summary>
/// Query to get the current agent build for a platform/architecture.
/// </summary>
public sealed record GetCurrentAgentBuildQuery(
    string? Platform,
    string? Architecture,
    string? ArtifactType
) : IQuery<Result<AgentBuildDto>>;

public sealed record AgentBuildDto(
    Guid Id,
    string Version,
    string Platform,
    string Architecture,
    string FileName,
    string Sha256,
    string? SignatureThumbprint,
    DateTime PublishedAt
);

/// <summary>
/// Query to list agent alerts.
/// </summary>
public sealed record ListAgentAlertsQuery(
    Guid? AgentId,
    Guid? ClientId,
    string? Severity,
    string? Status,
    string? Cursor,
    int Limit = 50
) : IQuery<Result<ListAgentAlertsResult>>;

public sealed record ListAgentAlertsResult(
    IReadOnlyList<AgentAlertDto> Alerts,
    string? NextCursor,
    bool HasMore
);

public sealed record AgentAlertDto(
    Guid Id,
    Guid AgentId,
    string Title,
    string Severity,
    string Status,
    DateTime CreatedAt
);

/// <summary>
/// Query to get P2P snapshot.
/// </summary>
public sealed record GetP2pSnapshotQuery(
    Guid? AgentId,
    Guid? SiteId,
    Guid? ClientId
) : IQuery<Result<P2pSnapshotDto>>;

public sealed record P2pSnapshotDto(
    int TotalPeers,
    int OnlinePeers,
    long TotalBytesShared,
    DateTime CollectedAt
);
