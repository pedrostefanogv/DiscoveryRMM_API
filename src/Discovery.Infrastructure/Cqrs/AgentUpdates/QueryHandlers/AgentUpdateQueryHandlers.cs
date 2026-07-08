using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentUpdates.QueryHandlers;

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
            build.FileName, build.Sha256, build.CreatedAt, null));
    }
}

public sealed class ListAgentUpdateEventsQueryHandler(
    IAgentUpdateEventRepository eventRepo
) : IRequestHandler<ListAgentUpdateEventsQuery, Result<List<AgentUpdateEventDto>>>
{
    public async Task<Result<List<AgentUpdateEventDto>>> Handle(ListAgentUpdateEventsQuery q, CancellationToken ct)
    {
        var events = await eventRepo.GetByAgentIdAsync(q.AgentId, q.Limit, ct);
        var dtos = events.Select(e => new AgentUpdateEventDto(e.Id, e.AgentId, e.EventType.ToString(), e.Message ?? "unknown", e.CreatedAt)).ToList();
        return Result<List<AgentUpdateEventDto>>.Success(dtos);
    }
}

public sealed class GetRolloutDashboardQueryHandler(
    IAgentUpdateService agentUpdateService
) : IRequestHandler<GetRolloutDashboardQuery, Result<RolloutDashboardDto>>
{
    public async Task<Result<RolloutDashboardDto>> Handle(GetRolloutDashboardQuery q, CancellationToken ct)
    {
        var dashboard = await agentUpdateService.GetRolloutDashboardAsync(q.ClientId, q.SiteId, q.Limit, ct);
        var recentEvents = dashboard.Agents.Select(a => new AgentUpdateEventDto(
            a.AgentId, a.AgentId, a.LatestEventType?.ToString() ?? "unknown",
            a.RolloutStatus, a.LastEventAtUtc ?? DateTime.UtcNow)).ToList();
        var summary = dashboard.Summary;
        return Result<RolloutDashboardDto>.Success(new RolloutDashboardDto(
            summary.TotalAgents, summary.Succeeded, (summary.Checking + summary.UpdateAvailable + summary.Downloading + summary.Installing),
            summary.Failed, recentEvents));
    }
}