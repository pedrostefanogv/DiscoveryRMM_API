using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class ApproveZeroTouchCommandHandler(
    IAgentRepository agentRepo,
    IAgentMessaging messaging
) : IRequestHandler<ApproveZeroTouchCommand, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(ApproveZeroTouchCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<AgentDto>.Failure(Error.NotFound("Agent not found."));
        if (!agent.ZeroTouchPending)
            return Result<AgentDto>.Failure(Error.Validation("AgentId", "Agent is not pending zero-touch approval."));

        await agentRepo.ApproveZeroTouchAsync(cmd.AgentId);

        var ping = new SyncInvalidationPingDto
        {
            EventId = Guid.NewGuid(), AgentId = cmd.AgentId, Resource = SyncResourceType.ZeroTouchApproved,
            ScopeType = AppApprovalScopeType.Agent, ScopeId = cmd.AgentId,
            Revision = $"zero-touch:{DateTime.UtcNow:O}", Reason = "zero-touch-approved", ChangedAtUtc = DateTime.UtcNow
        };
        await messaging.PublishSyncPingAsync(cmd.AgentId, SyncInvalidationPingMessage.FromDto(ping), ct);

        return Result<AgentDto>.Success(MapToDto(agent));
    }

    private static AgentDto MapToDto(Agent a) => new(a.Id, a.DisplayName ?? a.Hostname, Guid.Empty, a.SiteId, a.EffectiveStatus.ToString(), a.AgentVersion, a.MacAddress, a.CreatedAt, a.LastSeenAt);
}

public sealed class CreateAgentCommandHandler(
    IAgentRepository agentRepo,
    IRedisService redis,
    ISiteRepository siteRepo
) : IRequestHandler<CreateAgentCommand, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(CreateAgentCommand cmd, CancellationToken ct)
    {
        var agent = new Agent
        {
            SiteId = cmd.SiteId,
            Hostname = cmd.Name,
            DisplayName = cmd.Name,
            MacAddress = cmd.MacAddress
        };
        var created = await agentRepo.CreateAsync(agent);
        await InvalidateCachesAsync(redis, siteRepo, created.SiteId, null);
        return Result<AgentDto>.Success(MapToDto(created));
    }

    internal static async Task InvalidateCachesAsync(IRedisService redis, ISiteRepository siteRepo, Guid currentSiteId, Guid? previousSiteId, Guid? agentId = null)
    {
        await redis.DeleteAsync("agents:all-ids");
        await redis.DeleteByPrefixAsync("software-inventory:");
        var siteIds = new HashSet<Guid> { currentSiteId };
        if (previousSiteId.HasValue) siteIds.Add(previousSiteId.Value);
        foreach (var siteId in siteIds)
        {
            await redis.DeleteAsync($"agents:by-site:{siteId:N}");
            var site = await siteRepo.GetByIdAsync(siteId);
            if (site is not null)
                await redis.DeleteAsync($"agents:by-client:{site.ClientId:N}");
        }
        if (agentId.HasValue)
        {
            await redis.DeleteAsync($"agents:single:{agentId.Value:N}");
            await redis.DeleteAsync($"agents:hardware:{agentId.Value:N}");
            await redis.DeleteAsync($"agents:software:snapshot:{agentId.Value:N}");
        }
    }

    internal static AgentDto MapToDto(Agent a) => new(a.Id, a.DisplayName ?? a.Hostname, Guid.Empty, a.SiteId, a.EffectiveStatus.ToString(), a.AgentVersion, a.MacAddress, a.CreatedAt, a.LastSeenAt);
}

public sealed class UpdateAgentCommandHandler(
    IAgentRepository agentRepo,
    IRedisService redis,
    ISiteRepository siteRepo
) : IRequestHandler<UpdateAgentCommand, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(UpdateAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.Id);
        if (agent is null)
            return Result<AgentDto>.Failure(Error.NotFound("Agent not found."));

        var previousSiteId = agent.SiteId;
        if (cmd.SiteId.HasValue) agent.SiteId = cmd.SiteId.Value;
        if (cmd.Name is not null) { agent.Hostname = cmd.Name; agent.DisplayName = cmd.Name; }
        if (cmd.MacAddress is not null) agent.MacAddress = cmd.MacAddress;

        await agentRepo.UpdateAsync(agent);
        await CreateAgentCommandHandler.InvalidateCachesAsync(redis, siteRepo, agent.SiteId, previousSiteId);
        return Result<AgentDto>.Success(CreateAgentCommandHandler.MapToDto(agent));
    }
}

public sealed class DeleteAgentCommandHandler(
    IAgentRepository agentRepo,
    IAgentAuthService authService,
    IMeshCentralApiService meshCentral,
    IRedisService redis,
    ISiteRepository siteRepo,
    ILogger<DeleteAgentCommandHandler> logger
) : IRequestHandler<DeleteAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.Id);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        if (!string.IsNullOrWhiteSpace(agent.MeshCentralNodeId))
        {
            try { await meshCentral.RemoveDeviceAsync(agent.MeshCentralNodeId, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "MeshCentral cleanup failed for agent {AgentId} node {NodeId}", cmd.Id, agent.MeshCentralNodeId); }
        }

        await authService.RevokeAllTokensAsync(cmd.Id);
        await agentRepo.DeleteAsync(cmd.Id);
        await CreateAgentCommandHandler.InvalidateCachesAsync(redis, siteRepo, agent.SiteId, null, cmd.Id);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}