using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentP2p.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentP2p;

public sealed class GetP2pOverviewQueryHandler : IRequestHandler<GetP2pOverviewQuery, Result<IReadOnlyList<P2pOverviewDto>>>
{
    public Task<Result<IReadOnlyList<P2pOverviewDto>>> Handle(GetP2pOverviewQuery q, CancellationToken ct)
        => Task.FromResult(Result<IReadOnlyList<P2pOverviewDto>>.Success(Array.Empty<P2pOverviewDto>()));
}