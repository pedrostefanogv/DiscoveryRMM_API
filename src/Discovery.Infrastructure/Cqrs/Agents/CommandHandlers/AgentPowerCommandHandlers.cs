using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.PowerManagement.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class RestartAgentCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<RestartAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RestartAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var payload = JsonSerializer.Serialize(new { delaySeconds = 15, force = false, message = cmd.Reason });
        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.Restart, Payload = payload };
        await dispatcher.DispatchAsync(command, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ShutdownAgentCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<ShutdownAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ShutdownAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var payload = JsonSerializer.Serialize(new { delaySeconds = 30, force = false, message = cmd.Reason });
        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.Shutdown, Payload = payload };
        await dispatcher.DispatchAsync(command, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class WakeOnLanCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IAgentMessaging messaging
) : IRequestHandler<WakeOnLanCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(WakeOnLanCommand cmd, CancellationToken ct)
    {
        if (!messaging.IsConnected)
            return Result<VoidResult>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        if (string.IsNullOrWhiteSpace(agent.MacAddress))
            return Result<VoidResult>.Failure(Error.Validation("MacAddress", "Agent does not have a registered MAC address."));

        var allSiteAgents = await agentRepo.GetBySiteIdAsync(agent.SiteId);
        var onlineAgents = allSiteAgents.Where(a => a.Id != cmd.AgentId && a.EffectiveStatus == AgentStatus.Online).ToList();

        if (onlineAgents.Count == 0)
            return Result<VoidResult>.Failure(Error.Validation("SiteId", "No online agents available in the same site to relay the Wake-on-LAN packet."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        var expiresAtUtc = issuedAtUtc.AddSeconds(60);

        var wolPayload = JsonSerializer.Serialize(new { macAddress = agent.MacAddress, broadcastAddress = "255.255.255.255" });
        var envelope = new CommandDispatchEnvelope
        {
            DispatchId = dispatchId,
            CommandType = CommandTypeWireMapper.ToWireValue(CommandType.WakeOnLan),
            TargetScope = "site",
            TargetClientId = site?.ClientId,
            TargetSiteId = agent.SiteId,
            IssuedAtUtc = issuedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            IdempotencyKey = $"wol-{cmd.AgentId}-{dispatchId}",
            Payload = wolPayload
        };

        await messaging.PublishSiteFanoutCommandAsync(site?.ClientId ?? Guid.Empty, agent.SiteId, envelope, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}