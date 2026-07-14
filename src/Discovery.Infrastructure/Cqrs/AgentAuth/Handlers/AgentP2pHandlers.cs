using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.P2P;
using Discovery.Core.DTOs;
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

public sealed class IngestP2pTelemetryHandler(
    IP2pService p2pService
) : IRequestHandler<IngestP2pTelemetryCommand, Result<List<P2pErrorDetail>>>
{
    public async Task<Result<List<P2pErrorDetail>>> Handle(IngestP2pTelemetryCommand cmd, CancellationToken ct)
    {
        var errors = await p2pService.IngestTelemetryAsync(cmd.AgentId, cmd.Request, ct);
        return Result<List<P2pErrorDetail>>.Success(errors);
    }
}