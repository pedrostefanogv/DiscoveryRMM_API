using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Discovery.Api.Services;

/// <summary>
/// Dispatcher especializado para comandos de sessão remota.
/// Publica comandos start/stop/quality/recording no subject NATS do agent.
/// </summary>
public class RemoteSessionDispatcher
{
    private readonly ICommandRepository _commandRepository;
    private readonly IAgentMessaging _messaging;
    private readonly ILogger<RemoteSessionDispatcher> _logger;

    public RemoteSessionDispatcher(
        ICommandRepository commandRepository,
        IAgentMessaging messaging,
        ILogger<RemoteSessionDispatcher> logger)
    {
        _commandRepository = commandRepository;
        _messaging = messaging;
        _logger = logger;
    }

    /// <summary>
    /// Envia comando RemoteSessionStart para o agent.
    /// </summary>
    public async Task DispatchStartAsync(
        Guid agentId,
        Guid sessionId,
        RemoteSessionKind kind,
        RemoteTransport transport,
        QualityProfile quality,
        RemoteCodec codec,
        int durationMinutes,
        DateTime expiresAtUtc,
        string natsSubject,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "start",
            sessionId,
            kind = kind.ToString().ToLowerInvariant(),
            transport = transport.ToString().ToLowerInvariant(),
            quality = quality.ToString().ToLowerInvariant(),
            codec = codec.ToString().ToLowerInvariant(),
            durationMinutes,
            expiresAtUtc,
            natsSubject
        });

        await DispatchToAgentAsync(agentId, CommandType.RemoteSessionStart, payload, ct);
    }

    /// <summary>
    /// Envia comando RemoteSessionStop para o agent.
    /// </summary>
    public async Task DispatchStopAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { action = "stop", sessionId });
        await DispatchToAgentAsync(agentId, CommandType.RemoteSessionStop, payload, ct);
    }

    /// <summary>
    /// Envia comando de mudança de qualidade (imagem/FPS/codec) para o agent.
    /// </summary>
    public async Task DispatchQualityChangeAsync(
        Guid agentId,
        Guid sessionId,
        QualityProfile quality,
        RemoteCodec? codec = null,
        int? imageQuality = null,
        int? maxFps = null,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "quality",
            sessionId,
            quality = quality.ToString().ToLowerInvariant(),
            codec = codec?.ToString().ToLowerInvariant(),
            imageQuality,
            maxFps
        });

        await DispatchToAgentAsync(agentId, CommandType.RemoteSessionQuality, payload, ct);
    }

    /// <summary>
    /// Envia comando RecordingStart para o agent.
    /// </summary>
    public async Task DispatchRecordingStartAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { action = "recording_start", sessionId });
        await DispatchToAgentAsync(agentId, CommandType.RecordingStart, payload, ct);
    }

    /// <summary>
    /// Envia comando RecordingStop para o agent.
    /// </summary>
    public async Task DispatchRecordingStopAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { action = "recording_stop", sessionId });
        await DispatchToAgentAsync(agentId, CommandType.RecordingStop, payload, ct);
    }

    private async Task DispatchToAgentAsync(Guid agentId, CommandType commandType, string payload, CancellationToken ct)
    {
        var wireCommandType = CommandTypeWireMapper.ToWireValue(commandType);

        var command = new AgentCommand
        {
            AgentId = agentId,
            CommandType = commandType,
            Payload = payload
        };

        var created = await _commandRepository.CreateAsync(command);
        var sent = false;

        if (_messaging.IsConnected)
        {
            try
            {
                await _messaging.SendCommandAsync(agentId, created.Id, wireCommandType, payload);
                sent = true;
                created.SentAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RemoteSession dispatch via NATS failed for agent {AgentId}, command {CommandId}",
                    agentId, created.Id);
            }
        }

        if (sent)
        {
            created.Status = CommandStatus.Sent;
            await _commandRepository.UpdateStatusAsync(created.Id, CommandStatus.Sent, null, null, null);
        }
        else
        {
            _logger.LogWarning(
                "RemoteSession command {CommandId} queued for agent {AgentId} (NATS not connected)",
                created.Id, agentId);
        }
    }
}
