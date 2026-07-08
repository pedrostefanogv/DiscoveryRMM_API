using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentP2p.Queries;

public sealed record GetP2pOverviewQuery(string Scope, Guid? TenantId, Guid? SiteId, Guid? AgentId) : IQuery<Result<IReadOnlyList<P2pOverviewDto>>>;
public sealed record P2pOverviewDto(int TotalAgents, int ActivePeers, double Efficiency, DateTime SnapshotAt);