using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AgentP2p.Queries;

public sealed record GetP2pOverviewQuery(string Scope, Guid? TenantId, Guid? SiteId, Guid? AgentId, int WindowHours = 24) : IQuery<Result<P2pOverviewDto>>;
public sealed record GetP2pTimeseriesQuery(string Scope, Guid? TenantId, Guid? SiteId, Guid? AgentId, string Metric, int WindowHours = 24) : IQuery<Result<P2pTimeseriesDto>>;
public sealed record GetP2pAgentRankingQuery(string Scope, Guid? TenantId, Guid? SiteId, int WindowHours = 24, string SortBy = "peers") : IQuery<Result<List<P2pAgentRankingItem>>>;
public sealed record GetP2pSeedPlanQuery(string Scope, Guid? TenantId, Guid? SiteId) : IQuery<Result<List<P2pSeedPlanHistoryItem>>>;