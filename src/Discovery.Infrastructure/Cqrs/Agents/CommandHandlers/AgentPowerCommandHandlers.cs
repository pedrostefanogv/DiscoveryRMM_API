using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.PowerManagement.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class RestartAgentCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher,
    ISiteRepository siteRepo,
    IScopeContext scopeContext
) : IRequestHandler<RestartAgentCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RestartAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<Guid>.Failure(Error.NotFound("Agent not found."));

        // Recorte de tenant: restart/shutdown só no próprio escopo do usuário.
        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Execute);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return Result<Guid>.Failure(Error.Forbidden("Você não tem acesso a este agente."));

        var payload = JsonSerializer.Serialize(new
        {
            delaySeconds = cmd.DelaySeconds,
            force = cmd.Force,
            notifyUser = cmd.NotifyUser,
            message = cmd.Reason
        });
        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.Restart, Payload = payload };
        // O commandId volta para a UI acompanhar o desfecho real (executado, adiado,
        // negado, falhou) pelo histórico do agente.
        var dispatched = await dispatcher.DispatchAsync(command, ct);
        return Result<Guid>.Success(dispatched.Id);
    }
}

public sealed class ShutdownAgentCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher,
    ISiteRepository siteRepo,
    IScopeContext scopeContext
) : IRequestHandler<ShutdownAgentCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ShutdownAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<Guid>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Execute);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return Result<Guid>.Failure(Error.Forbidden("Você não tem acesso a este agente."));

        var payload = JsonSerializer.Serialize(new
        {
            delaySeconds = cmd.DelaySeconds,
            force = cmd.Force,
            notifyUser = cmd.NotifyUser,
            message = cmd.Reason
        });
        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.Shutdown, Payload = payload };
        // Mesmo contrato do restart: devolve o commandId para acompanhar o desfecho.
        var dispatched = await dispatcher.DispatchAsync(command, ct);
        return Result<Guid>.Success(dispatched.Id);
    }
}

/// <summary>
/// Comando genérico de energia (CommandType.PowerAction). Mesmo recorte de
/// tenant e mesmo contrato de retorno (commandId) que restart/shutdown.
/// </summary>
public sealed class PowerActionCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher,
    ISiteRepository siteRepo,
    IScopeContext scopeContext
) : IRequestHandler<PowerActionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(PowerActionCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<Guid>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Execute);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return Result<Guid>.Failure(Error.Forbidden("Você não tem acesso a este agente."));

        var payload = JsonSerializer.Serialize(new
        {
            action = cmd.Action,
            delaySeconds = cmd.DelaySeconds,
            force = cmd.Force,
            notifyUser = cmd.NotifyUser,
            message = cmd.Reason
        });
        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.PowerAction, Payload = payload };
        var dispatched = await dispatcher.DispatchAsync(command, ct);
        return Result<Guid>.Success(dispatched.Id);
    }
}

public sealed class WakeOnLanCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IAgentMessaging messaging,
    IScopeContext scopeContext
) : IRequestHandler<WakeOnLanCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(WakeOnLanCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Execute);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return Result<VoidResult>.Failure(Error.Forbidden("Você não tem acesso a este agente."));

        if (!messaging.IsConnected)
            return Result<VoidResult>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        if (string.IsNullOrWhiteSpace(agent.MacAddress))
            return Result<VoidResult>.Failure(Error.Validation("MacAddress", "Agent does not have a registered MAC address."));

        var allSiteAgents = await agentRepo.GetBySiteIdAsync(agent.SiteId);
        var onlineAgents = allSiteAgents.Where(a => a.Id != cmd.AgentId && a.EffectiveStatus == AgentStatus.Online).ToList();

        if (onlineAgents.Count == 0)
            return Result<VoidResult>.Failure(Error.Validation("SiteId", "No online agents available in the same site to relay the Wake-on-LAN packet."));

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