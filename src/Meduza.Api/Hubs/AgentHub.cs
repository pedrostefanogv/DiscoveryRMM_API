using System.Collections.Concurrent;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Meduza.Api.Hubs;

public class AgentHub : Hub
{
    private static readonly ConcurrentDictionary<string, Guid> ConnectedAgents = new();
    private readonly IAgentRepository _agentRepo;
    private readonly ICommandRepository _commandRepo;

    public AgentHub(IAgentRepository agentRepo, ICommandRepository commandRepo)
    {
        _agentRepo = agentRepo;
        _commandRepo = commandRepo;
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
            await Clients.Group("dashboard").SendAsync("AgentStatusChanged", agentId, AgentStatus.Offline);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Agent se registra ao conectar, informando seu ID.
    /// </summary>
    public async Task RegisterAgent(Guid agentId, string? ipAddress)
    {
        ConnectedAgents[Context.ConnectionId] = agentId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"agent-{agentId}");
        await _agentRepo.UpdateStatusAsync(agentId, AgentStatus.Online, ipAddress);
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
        await _agentRepo.UpdateStatusAsync(agentId, AgentStatus.Online, ipAddress);
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

    /// <summary>
    /// Retorna a connection ID de um agent conectado.
    /// </summary>
    public static string? GetConnectionId(Guid agentId)
    {
        return ConnectedAgents.FirstOrDefault(x => x.Value == agentId).Key;
    }
}
