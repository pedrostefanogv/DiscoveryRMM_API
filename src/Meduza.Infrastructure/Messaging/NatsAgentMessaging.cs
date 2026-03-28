using System.Text.Json;
using System.Text.Json.Serialization;
using Meduza.Core.DTOs;
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
    private readonly ISiteRepository _siteRepo;
    private readonly IAgentAuthService _agentAuthService;
    private readonly ILogger<NatsAgentMessaging> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public NatsAgentMessaging(
        NatsConnection connection,
        IAgentRepository agentRepo,
        ICommandRepository commandRepo,
        ISiteRepository siteRepo,
        IAgentAuthService agentAuthService,
        ILogger<NatsAgentMessaging> logger)
    {
        _connection = connection;
        _agentRepo = agentRepo;
        _commandRepo = commandRepo;
        _siteRepo = siteRepo;
        _agentAuthService = agentAuthService;
        _logger = logger;
    }

    /// <summary>
    /// Verifica que o agent possui ao menos um token ativo.
    /// Defesa contra spoofing via NATS: mensagens de AgentIds sem token válido são descartadas.
    /// </summary>
    private async Task<bool> IsAgentAuthorizedAsync(Guid agentId)
    {
        var tokens = await _agentAuthService.GetTokensByAgentIdAsync(agentId);
        return tokens.Any(t => t.IsValid);
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

    public async Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var payload = JsonSerializer.Serialize(message, JsonOptions);

        await _connection.PublishAsync("dashboard.events", payload);
    }

    public async Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var subject = $"agent.{agentId}.sync.ping";
        var message = JsonSerializer.Serialize(ping, JsonOptions);
        await _connection.PublishAsync(subject, message);
        _logger.LogDebug("Sync ping published to agent {AgentId} for resource {Resource} (revision {Revision})",
            agentId,
            ping.Resource,
            ping.Revision);
    }

    public async Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("NATS subscription tasks starting...");

        // Subscrever heartbeats: agent.*.heartbeat
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in _connection.SubscribeAsync<string>("agent.*.heartbeat", cancellationToken: cancellationToken))
                {
                    try
                    {
                        var agentId = ExtractAgentId(msg.Subject);
                        if (agentId.HasValue)
                        {
                            if (!await IsAgentAuthorizedAsync(agentId.Value))
                            {
                                _logger.LogWarning("NATS: heartbeat rejeitado de agent sem token válido (possível spoofing) — AgentId={AgentId}, Subject={Subject}", agentId.Value, msg.Subject);
                                continue;
                            }
                            var heartbeat = JsonSerializer.Deserialize<HeartbeatMessage>(msg.Data ?? "", JsonOptions);
                            await _agentRepo.UpdateStatusAsync(agentId.Value, AgentStatus.Online, heartbeat?.IpAddress);
                            _logger.LogDebug("Heartbeat processed from agent {AgentId} (IP: {IpAddress})", agentId.Value, heartbeat?.IpAddress);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing heartbeat from {Subject}", msg.Subject);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Heartbeat subscription cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in heartbeat subscription");
            }
        }, cancellationToken);

        // Subscrever resultados: agent.*.result
        var resultTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in _connection.SubscribeAsync<string>("agent.*.result", cancellationToken: cancellationToken))
                {
                    try
                    {
                        var agentId = ExtractAgentId(msg.Subject);
                        if (agentId.HasValue && !await IsAgentAuthorizedAsync(agentId.Value))
                        {
                            _logger.LogWarning("NATS: command result rejeitado de agent sem token válido (possível spoofing) — AgentId={AgentId}, Subject={Subject}", agentId.Value, msg.Subject);
                            continue;
                        }
                        var result = JsonSerializer.Deserialize<CommandResultMessage>(msg.Data ?? "", JsonOptions);
                        if (result is not null)
                        {
                            var status = result.ExitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed;
                            await _commandRepo.UpdateStatusAsync(result.CommandId, status, result.Output, result.ExitCode, result.ErrorMessage);
                            await PublishDashboardEventForAgentAsync(
                                agentId,
                                "CommandCompleted",
                                result,
                                cancellationToken);
                            _logger.LogDebug("Command result processed: {CommandId} - Exit Code: {ExitCode}", result.CommandId, result.ExitCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing command result from {Subject}", msg.Subject);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Command result subscription cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in result subscription");
            }
        }, cancellationToken);

        // Subscrever reports de hardware: agent.*.hardware
        var hardwareTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in _connection.SubscribeAsync<string>("agent.*.hardware", cancellationToken: cancellationToken))
                {
                    try
                    {
                        var agentId = ExtractAgentId(msg.Subject);
                        if (agentId.HasValue)
                        {
                            if (!await IsAgentAuthorizedAsync(agentId.Value))
                            {
                                _logger.LogWarning("NATS: hardware report rejeitado de agent sem token válido (possível spoofing) — AgentId={AgentId}, Subject={Subject}", agentId.Value, msg.Subject);
                                continue;
                            }
                            _logger.LogDebug("Hardware report received from agent {AgentId}", agentId.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing hardware report from {Subject}", msg.Subject);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Hardware subscription cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in hardware subscription");
            }
        }, cancellationToken);

        _logger.LogInformation("NATS agent message subscriptions started successfully");

        // Mantém o método ativo enquanto as subscriptions estiverem vivas.
        await Task.WhenAll(heartbeatTask, resultTask, hardwareTask);
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
        // NatsConnection é singleton no DI e não deve ser descartada por este serviço scoped.
        await ValueTask.CompletedTask;
    }

    private async Task PublishDashboardEventForAgentAsync(
        Guid? agentId,
        string eventType,
        object data,
        CancellationToken cancellationToken)
    {
        Guid? clientId = null;
        Guid? siteId = null;

        if (agentId.HasValue)
        {
            var agent = await _agentRepo.GetByIdAsync(agentId.Value);
            if (agent is not null)
            {
                siteId = agent.SiteId;
                var site = await _siteRepo.GetByIdAsync(agent.SiteId);
                clientId = site?.ClientId;
            }
        }

        var message = DashboardEventMessage.Create(eventType, data, clientId, siteId);
        await PublishDashboardEventAsync(message, cancellationToken);
    }

    private record HeartbeatMessage(string? IpAddress, string? AgentVersion);
    private record CommandResultMessage(Guid CommandId, int ExitCode, string? Output, string? ErrorMessage);
}
