using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Maintenance.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class SetAgentMaintenanceCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IHeartbeatCacheService heartbeatCache,
    IAgentMessaging messaging
) : IRequestHandler<SetAgentMaintenanceCommand, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(SetAgentMaintenanceCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<AgentDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return Result<AgentDto>.Failure(Error.NotFound("Site not found."));

        // userId is not in the command, use Guid.Empty as placeholder
        await agentRepo.SetMaintenanceAsync(cmd.AgentId, cmd.Enabled, cmd.Reason, Guid.Empty);

        var effectiveStatus = cmd.Enabled ? "Maintenance" : await RecalculateEffectiveStatusAsync(cmd.AgentId, heartbeatCache);

        await messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create("AgentStatusChanged", new
            {
                agentId = cmd.AgentId,
                maintenanceEnabled = cmd.Enabled,
                effectiveStatus,
                changedAtUtc = DateTime.UtcNow
            }, site.ClientId, agent.SiteId), ct);

        return Result<AgentDto>.Success(CreateAgentCommandHandler.MapToDto(agent));
    }

    private static async Task<string> RecalculateEffectiveStatusAsync(Guid agentId, IHeartbeatCacheService heartbeatCache)
    {
        var heartbeat = await heartbeatCache.GetHeartbeatAsync(agentId);
        return heartbeat is not null ? "Online" : "Offline";
    }
}