using System.Text.Json;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace Meduza.Infrastructure.Messaging;

/// <summary>
/// Implementação NATS para comunicação em tempo real com agents.
/// Subjects:
///   agent.{agentId}.command   → Servidor → Agent (enviar comando)
///   agent.{agentId}.heartbeat → Agent → Servidor (heartbeat)
///   agent.{agentId}.result    → Agent → Servidor (resultado de comando)
///   agent.{agentId}.hardware  → Agent → Servidor (hw report)
///   dashboard.events          → Servidor → Dashboard (eventos broadcast)
/// </summary>
public class NatsAgentMessaging : IAgentMessaging, IAsyncDisposable
{
    private readonly NatsConnection _connection;
    private readonly IAgentRepository _agentRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly ILogger<NatsAgentMessaging> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public NatsAgentMessaging(
        NatsConnection connection,
        IAgentRepository agentRepo,
        ICommandRepository commandRepo,
        ILogger<NatsAgentMessaging> logger)
    {
        _connection = connection;
        _agentRepo = agentRepo;
        _commandRepo = commandRepo;
        _logger = logger;
    }

    public bool IsConnected => _connection.ConnectionState == NatsConnectionState.Open;

    public async Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload)
    {
        var subject = $"agent.{agentId}.command";
        var message = JsonSerializer.Serialize(new
        {
            CommandId = commandId,
            CommandType = commandType,
            Payload = payload
        }, JsonOptions);

        await _connection.PublishAsync(subject, message);
        _logger.LogDebug("Command {CommandId} sent to agent {AgentId} via NATS", commandId, agentId);
    }

    public async Task PublishDashboardEventAsync(string eventType, object data)
    {
        var message = JsonSerializer.Serialize(new
        {
            EventType = eventType,
            Data = data,
            Timestamp = DateTime.UtcNow
        }, JsonOptions);

        await _connection.PublishAsync("dashboard.events", message);
    }

    public async Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken)
    {
        // Subscrever heartbeats: agent.*.heartbeat
        _ = Task.Run(async () =>
        {
            await foreach (var msg in _connection.SubscribeAsync<string>("agent.*.heartbeat", cancellationToken: cancellationToken))
            {
                try
                {
                    var agentId = ExtractAgentId(msg.Subject);
                    if (agentId.HasValue)
                    {
                        var heartbeat = JsonSerializer.Deserialize<HeartbeatMessage>(msg.Data ?? "", JsonOptions);
                        await _agentRepo.UpdateStatusAsync(agentId.Value, AgentStatus.Online, heartbeat?.IpAddress);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing heartbeat from {Subject}", msg.Subject);
                }
            }
        }, cancellationToken);

        // Subscrever resultados: agent.*.result
        _ = Task.Run(async () =>
        {
            await foreach (var msg in _connection.SubscribeAsync<string>("agent.*.result", cancellationToken: cancellationToken))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<CommandResultMessage>(msg.Data ?? "", JsonOptions);
                    if (result is not null)
                    {
                        var status = result.ExitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed;
                        await _commandRepo.UpdateStatusAsync(result.CommandId, status, result.Output, result.ExitCode, result.ErrorMessage);
                        await PublishDashboardEventAsync("CommandCompleted", result);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing command result from {Subject}", msg.Subject);
                }
            }
        }, cancellationToken);

        _logger.LogInformation("NATS agent message subscriptions started");
    }

    private static Guid? ExtractAgentId(string subject)
    {
        // subject format: agent.{guid}.heartbeat
        var parts = subject.Split('.');
        if (parts.Length >= 2 && Guid.TryParse(parts[1], out var id))
            return id;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private record HeartbeatMessage(string? IpAddress, string? AgentVersion);
    private record CommandResultMessage(Guid CommandId, int ExitCode, string? Output, string? ErrorMessage);
}
