using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.P2P;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentP2pSeedPlanHandler(
    IP2pService p2pService
) : IRequestHandler<GetAgentP2pSeedPlanQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentP2pSeedPlanQuery q, CancellationToken ct)
    {
        var plan = await p2pService.GetSeedPlanAsync(q.AgentId, ct);
        return Result<object>.Success(plan);
    }
}