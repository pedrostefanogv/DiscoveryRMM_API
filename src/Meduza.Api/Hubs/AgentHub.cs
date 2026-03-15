using System.Collections.Concurrent;
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
    private readonly ICommandRepository _commandRepo;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(IAgentRepository agentRepo, ICommandRepository commandRepo, ILogger<AgentHub> logger)
    {
        _agentRepo = agentRepo;
        _commandRepo = commandRepo;
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
            await _agentRepo.UpdateStatusAsync(agentId, AgentStatus.Offline, null);
            LastPersistedHeartbeat.TryRemove(agentId, out _);
            await Clients.Group("dashboard").SendAsync("AgentStatusChanged", agentId, AgentStatus.Offline);
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
        await Clients.Group("dashboard").SendAsync("AgentStatusChanged", agentId, AgentStatus.Online);

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
        await Clients.Group("dashboard").SendAsync("CommandCompleted", commandId, exitCode, output, errorMessage);
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
        await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
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

    private readonly record struct HeartbeatState(DateTime LastPersistedAt, string? LastIpAddress);
}
