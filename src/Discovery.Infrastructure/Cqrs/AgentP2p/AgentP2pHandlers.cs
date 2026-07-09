using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentP2p.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentP2p;

public sealed class GetP2pOverviewQueryHandler(IP2pService p2p) : IRequestHandler<GetP2pOverviewQuery, Result<P2pOverviewDto>>
{
    public async Task<Result<P2pOverviewDto>> Handle(GetP2pOverviewQuery q, CancellationToken ct)
    {
        var window = TimeSpan.FromHours(q.WindowHours);
        var result = await p2p.GetOverviewAsync(q.Scope, q.TenantId, q.SiteId, q.AgentId, window, ct);
        return Result<P2pOverviewDto>.Success(result);
    }
}

public sealed class GetP2pTimeseriesQueryHandler(IP2pService p2p) : IRequestHandler<GetP2pTimeseriesQuery, Result<P2pTimeseriesDto>>
{
    public async Task<Result<P2pTimeseriesDto>> Handle(GetP2pTimeseriesQuery q, CancellationToken ct)
    {
        var to = DateTime.UtcNow;
        var from = to.AddHours(-q.WindowHours);
        var interval = TimeSpan.FromMinutes(Math.Max(5, q.WindowHours * 60 / 50));
        var result = await p2p.GetTimeseriesAsync(q.Scope, q.TenantId, q.SiteId, q.AgentId, q.Metric, from, to, interval, ct);
        return Result<P2pTimeseriesDto>.Success(result);
    }
}

public sealed class GetP2pAgentRankingQueryHandler(IP2pService p2p) : IRequestHandler<GetP2pAgentRankingQuery, Result<List<P2pAgentRankingItem>>>
{
    public async Task<Result<List<P2pAgentRankingItem>>> Handle(GetP2pAgentRankingQuery q, CancellationToken ct)
    {
        var window = TimeSpan.FromHours(q.WindowHours);
        var result = await p2p.GetAgentRankingAsync(q.Scope, q.TenantId, q.SiteId, window, q.SortBy, ct);
        return Result<List<P2pAgentRankingItem>>.Success(result);
    }
}

public sealed class GetP2pSeedPlanQueryHandler(IP2pService p2p) : IRequestHandler<GetP2pSeedPlanQuery, Result<List<P2pSeedPlanHistoryItem>>>
{
    public async Task<Result<List<P2pSeedPlanHistoryItem>>> Handle(GetP2pSeedPlanQuery q, CancellationToken ct)
    {
        var result = await p2p.GetSeedPlanStatusAsync(q.Scope, q.TenantId, q.SiteId, ct);
        return Result<List<P2pSeedPlanHistoryItem>>.Success(result);
    }
}