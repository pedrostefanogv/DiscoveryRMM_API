using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetCurrentAgentBuildQueryHandler(
    IAgentUpdateService agentUpdateService
) : IRequestHandler<GetCurrentAgentBuildQuery, Result<AgentBuildDto>>
{
    public async Task<Result<AgentBuildDto>> Handle(GetCurrentAgentBuildQuery q, CancellationToken ct)
    {
        var artifactType = q.ArtifactType is not null && Enum.TryParse<AgentReleaseArtifactType>(q.ArtifactType, true, out var at)
            ? at : (AgentReleaseArtifactType?)null;

        var build = await agentUpdateService.GetCurrentBuildAsync(q.Platform, q.Architecture, artifactType, ct);
        if (build is null)
            return Result<AgentBuildDto>.Failure(Error.NotFound("No current agent build found"));

        return Result<AgentBuildDto>.Success(new AgentBuildDto(
            build.Id, build.Version, build.Platform, build.Architecture,
            build.FileName, build.Sha256, build.SignatureThumbprint, build.CreatedAt));
    }
}

public sealed class ListAgentAlertsQueryHandler(
    IAgentAlertService alertService
) : IRequestHandler<ListAgentAlertsQuery, Result<ListAgentAlertsResult>>
{
    public async Task<Result<ListAgentAlertsResult>> Handle(ListAgentAlertsQuery q, CancellationToken ct)
    {
        var status = q.Status is not null && Enum.TryParse<AlertDefinitionStatus>(q.Status, true, out var st) ? st : (AlertDefinitionStatus?)null;

        var items = await alertService.GetAllPageAsync(
            status, null, q.ClientId, null, q.AgentId, null, q.Cursor, q.Limit);

        var dtos = items.Select(a => new AgentAlertDto(
            a.Id, a.ScopeAgentId ?? Guid.Empty, a.Title,
            a.AlertType.ToString(), a.Status.ToString(), a.CreatedAt
        )).ToList() as IReadOnlyList<AgentAlertDto>;

        return Result<ListAgentAlertsResult>.Success(new ListAgentAlertsResult(dtos, null, false));
    }
}

public sealed class GetP2pSnapshotQueryHandler(
    IP2pService p2pService
) : IRequestHandler<GetP2pSnapshotQuery, Result<P2pSnapshotDto>>
{
    public async Task<Result<P2pSnapshotDto>> Handle(GetP2pSnapshotQuery q, CancellationToken ct)
    {
        // P2P snapshot real requer um agentId específico para obter o seed plan.
        // Como o query permite filtro opcional, retornamos um snapshot vazio se sem agentId.
        if (q.AgentId is null)
            return Result<P2pSnapshotDto>.Success(new P2pSnapshotDto(0, 0, 0, DateTime.UtcNow));

        var seedPlan = await p2pService.GetSeedPlanAsync(q.AgentId.Value, ct);
        return Result<P2pSnapshotDto>.Success(new P2pSnapshotDto(
            seedPlan.Plan.TotalAgents,
            seedPlan.Plan.SelectedSeeds,
            0,
            DateTime.UtcNow));
    }
}