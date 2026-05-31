using Discovery.Api.Filters;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Power management endpoints: restart, shutdown, and Wake-on-LAN for agents.
/// </summary>
public partial class AgentsController
{
    /// <summary>
    /// Sends a restart command to a specific agent.
    /// The agent will schedule a system reboot after the configured delay.
    /// </summary>
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    [HttpPost("{id:guid}/restart")]
    public async Task<IActionResult> RestartAgent(Guid id, [FromBody] RestartRequest? request)
    {
        request ??= new RestartRequest();

        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound(new { error = "Agent not found." });

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            delaySeconds = request.DelaySeconds,
            force = request.Force,
            message = request.Message
        });

        var command = new AgentCommand
        {
            AgentId = id,
            CommandType = CommandType.Restart,
            Payload = payload
        };

        var created = await _commandDispatcher.DispatchAsync(command);
        return Accepted(new
        {
            commandId = created.Id,
            agentId = id,
            commandType = "restart",
            delaySeconds = request.DelaySeconds,
            force = request.Force,
            status = created.Status.ToString()
        });
    }

    /// <summary>
    /// Sends a shutdown command to a specific agent.
    /// The agent will schedule a system shutdown after the configured delay.
    /// </summary>
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    [HttpPost("{id:guid}/shutdown")]
    public async Task<IActionResult> ShutdownAgent(Guid id, [FromBody] ShutdownRequest? request)
    {
        request ??= new ShutdownRequest();

        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound(new { error = "Agent not found." });

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            delaySeconds = request.DelaySeconds,
            force = request.Force,
            message = request.Message
        });

        var command = new AgentCommand
        {
            AgentId = id,
            CommandType = CommandType.Shutdown,
            Payload = payload
        };

        var created = await _commandDispatcher.DispatchAsync(command);
        return Accepted(new
        {
            commandId = created.Id,
            agentId = id,
            commandType = "shutdown",
            delaySeconds = request.DelaySeconds,
            force = request.Force,
            status = created.Status.ToString()
        });
    }

    /// <summary>
    /// Sends a Wake-on-LAN magic packet to an offline agent.
    /// The WOL command is fan-out to all ONLINE agents in the same site,
    /// ensuring redundancy — multiple agents will send the magic packet.
    /// </summary>
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    [HttpPost("{id:guid}/wake-on-lan")]
    public async Task<IActionResult> WakeOnLan(Guid id, [FromBody] WakeOnLanRequest? request, CancellationToken cancellationToken = default)
    {
        request ??= new WakeOnLanRequest();

        if (!_messaging.IsConnected)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "NATS realtime transport unavailable." });

        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound(new { error = "Agent not found." });

        if (string.IsNullOrWhiteSpace(agent.MacAddress))
            return UnprocessableEntity(new { error = "Agent does not have a registered MAC address. Wake-on-LAN requires a MAC address." });

        // Get all agents in the same site, then filter to online ones
        var allSiteAgents = await _agentRepo.GetBySiteIdAsync(agent.SiteId);
        var onlineAgents = allSiteAgents
            .Where(a => a.Id != id && a.EffectiveStatus == AgentStatus.Online)
            .ToList();

        if (onlineAgents.Count == 0)
            return StatusCode(StatusCodes.Status412PreconditionFailed, new
            {
                error = "No online agents available in the same site to relay the Wake-on-LAN packet. At least one agent must be online to send the magic packet.",
                siteId = agent.SiteId
            });

        var site = await _siteRepository.GetByIdAsync(agent.SiteId);

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        var expiresAtUtc = issuedAtUtc.AddSeconds(60);

        var wolPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            macAddress = agent.MacAddress,
            broadcastAddress = request.BroadcastAddress
        });

        var envelope = new CommandDispatchEnvelope
        {
            DispatchId = dispatchId,
            CommandId = null,
            CommandType = CommandTypeWireMapper.ToWireValue(CommandType.WakeOnLan),
            TargetScope = "site",
            TargetClientId = site?.ClientId,
            TargetSiteId = agent.SiteId,
            IssuedAtUtc = issuedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            IdempotencyKey = $"wol-{id}-{dispatchId}",
            Payload = wolPayload
        };

        await _messaging.PublishSiteFanoutCommandAsync(
            site?.ClientId ?? Guid.Empty,
            agent.SiteId,
            envelope,
            cancellationToken);

        return Accepted(new
        {
            dispatchId,
            onlineAgentsInSite = onlineAgents.Count,
            onlineAgentHostnames = onlineAgents.Select(a => a.Hostname).ToList(),
            targetMacAddress = agent.MacAddress,
            targetHostname = agent.Hostname,
            broadcastAddress = request.BroadcastAddress ?? "255.255.255.255",
            expiresAtUtc
        });
    }
}

// ── Request DTOs ──

public record RestartRequest(
    int DelaySeconds = 15,
    bool Force = false,
    string? Message = null);

public record ShutdownRequest(
    int DelaySeconds = 30,
    bool Force = false,
    string? Message = null);

public record WakeOnLanRequest(
    string? BroadcastAddress = null);
