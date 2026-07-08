using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentUpdates.Queries;

public sealed record GetCurrentAgentBuildQuery(
    string? Platform, string? Architecture, string? ArtifactType
) : IQuery<Result<AgentBuildDto>>;

public sealed record AgentBuildDto(
    Guid Id, string Version, string Platform, string Architecture,
    string FileName, string Sha256, DateTime PublishedAt, string? SignatureThumbprint
);

public sealed record ListAgentUpdateEventsQuery(Guid AgentId, int Limit = 100)
    : IQuery<Result<List<AgentUpdateEventDto>>>;

public sealed record AgentUpdateEventDto(
    Guid Id, Guid AgentId, string EventType, string Status, DateTime CreatedAt
);

public sealed record GetRolloutDashboardQuery(
    Guid? ClientId, Guid? SiteId, int Limit = 200
) : IQuery<Result<RolloutDashboardDto>>;

public sealed record RolloutDashboardDto(
    int TotalAgents, int Updated, int Pending, int Failed, List<AgentUpdateEventDto> RecentEvents
);
