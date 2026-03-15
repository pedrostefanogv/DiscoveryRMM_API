using System.Collections.Concurrent;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Meduza.Api.Hubs;

public class AgentHub : Hub
{
    private static readonly ConcurrentDictionary<string, Guid> ConnectedAgents = new();
    private static readonly ConcurrentDictionary<Guid, HeartbeatState> LastPersistedHeartbeat = new();
    private static readonly TimeSpan HeartbeatWriteInterval = TimeSpan.FromSeconds(15);

    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly IAgentMessaging _messaging;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IClientRepository clientRepo,
        ICommandRepository commandRepo,
        IAgentMessaging messaging,
        ILogger<AgentHub> logger)
    {
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _clientRepo = clientRepo;
        _commandRepo = commandRepo;
        _messaging = messaging;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (ConnectedAgents.TryRemove(Context.ConnectionId, out var agentId))
        {
            var agent = await _agentRepo.GetByIdAsync(agentId);
            await _agentRepo.UpdateStatusAsync(agentId, AgentStatus.Offline, null);
            LastPersistedHeartbeat.TryRemove(agentId, out _);
            await PublishAgentStatusChangedAsync(agent ?? new() { Id = agentId }, AgentStatus.Offline);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Agent se registra ao conectar, informando seu ID.
    /// </summary>
    public async Task RegisterAgent(Guid agentId, string? ipAddress)
    {
        await EnsureConnectionMappedAsync(agentId);
        await _agentRepo.UpdateStatusAsync(agentId, AgentStatus.Online, ipAddress);
        LastPersistedHeartbeat[agentId] = new HeartbeatState(DateTime.UtcNow, ipAddress);
        var agent = await _agentRepo.GetByIdAsync(agentId) ?? new Agent { Id = agentId };
        await PublishAgentStatusChangedAsync(agent, AgentStatus.Online);

        // Envia comandos pendentes
        var pendingCommands = await _commandRepo.GetPendingByAgentIdAsync(agentId);
        foreach (var cmd in pendingCommands)
        {
            await Clients.Caller.SendAsync("ExecuteCommand", cmd.Id, cmd.CommandType, cmd.Payload);
            await _commandRepo.UpdateStatusAsync(cmd.Id, CommandStatus.Sent, null, null, null);
        }
    }

    /// <summary>
    /// Agent informa resultado de um comando executado.
    /// </summary>
    public async Task CommandResult(Guid commandId, int exitCode, string? output, string? errorMessage)
    {
        var status = exitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed;
        await _commandRepo.UpdateStatusAsync(commandId, status, output, exitCode, errorMessage);
        var command = await _commandRepo.GetByIdAsync(commandId);
        Guid? clientId = null;
        Guid? siteId = null;

        if (command is not null)
        {
            var agent = await _agentRepo.GetByIdAsync(command.AgentId);
            siteId = agent?.SiteId;
            if (siteId.HasValue)
            {
                var site = await _siteRepo.GetByIdAsync(siteId.Value);
                clientId = site?.ClientId;
            }
        }

        await _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(
                "CommandCompleted",
                new { commandId, exitCode, output, errorMessage },
                clientId,
                siteId));
    }

    /// <summary>
    /// Agent envia heartbeat periódico.
    /// </summary>
    public async Task Heartbeat(Guid agentId, string? ipAddress)
    {
        await EnsureConnectionMappedAsync(agentId);

        var now = DateTime.UtcNow;

        if (LastPersistedHeartbeat.TryGetValue(agentId, out var state)
            && now - state.LastPersistedAt < HeartbeatWriteInterval
            && string.Equals(state.LastIpAddress, ipAddress, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _agentRepo.UpdateStatusAsync(agentId, AgentStatus.Online, ipAddress);
            LastPersistedHeartbeat[agentId] = new HeartbeatState(now, ipAddress);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Heartbeat update timed out for agent {AgentId}.", agentId);
        }
    }

    /// <summary>
    /// Dashboard se inscreve para receber atualizações em tempo real.
    /// </summary>
    public async Task JoinDashboard()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroupNames.Global);
    }

    public async Task JoinClientDashboard(Guid clientId)
    {
        var client = await _clientRepo.GetByIdAsync(clientId);
        if (client is null)
            throw new HubException("Client not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroupNames.ForClient(clientId));
    }

    public async Task JoinSiteDashboard(Guid clientId, Guid siteId)
    {
        var site = await _siteRepo.GetByIdAsync(siteId);
        if (site is null || site.ClientId != clientId)
            throw new HubException("Site not found for this client.");

        await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroupNames.ForSite(siteId));
    }

    /// <summary>
    /// Verifica se um agent específico está conectado.
    /// </summary>
    public static bool IsAgentConnected(Guid agentId)
    {
        return ConnectedAgents.Values.Contains(agentId);
    }

    public static int ConnectedAgentCount => ConnectedAgents.Count;

    /// <summary>
    /// Retorna a connection ID de um agent conectado.
    /// </summary>
    public static string? GetConnectionId(Guid agentId)
    {
        return ConnectedAgents.FirstOrDefault(x => x.Value == agentId).Key;
    }

    private async Task EnsureConnectionMappedAsync(Guid agentId)
    {
        if (ConnectedAgents.TryGetValue(Context.ConnectionId, out var existingAgentId))
        {
            if (existingAgentId == agentId)
                return;

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"agent-{existingAgentId}");
        }

        ConnectedAgents[Context.ConnectionId] = agentId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"agent-{agentId}");
    }

    private async Task PublishAgentStatusChangedAsync(Agent agent, AgentStatus status)
    {
        Guid? clientId = null;
        Guid? siteId = agent.SiteId == Guid.Empty ? null : agent.SiteId;

        if (siteId.HasValue)
        {
            var site = await _siteRepo.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }

        await _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(
                "AgentStatusChanged",
                new { agentId = agent.Id, status = status.ToString() },
                clientId,
                siteId));
    }

    private readonly record struct HeartbeatState(DateTime LastPersistedAt, string? LastIpAddress);
}
