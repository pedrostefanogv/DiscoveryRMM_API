using Discovery.Core.Configuration;
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
    /// Resolve o teto (kbps) que o START publica para o gate do agent.
    ///
    /// O bloco `bandwidth` do start e o MESMO contrato consumido em runtime pela
    /// ação "bandwidth" (ver ChangeRemoteSessionBandwidthCommandHandler), e o
    /// agente trata os dois no mesmo ponto (SessionScreen.ConfigureBandwidth).
    /// Logo o campo `ceilingKbps` precisa significar a MESMA coisa nos dois:
    ///
    ///   - manual -> ManualDefaultKbps (teto fixo padrão);
    ///   - auto   -> AutoStartKbps (ponto de PARTIDA da escada automática);
    ///   - hd     -> 0 (HD não tem teto próprio).
    ///
    /// BUG CORRIGIDO (2026-11-10): antes enviava-se ManualDefaultKbps em QUALQUER
    /// modo. Com DefaultMode="auto", a sessão que NASCIA em auto publicava
    /// ceilingKbps=8000 (o default MANUAL) e nunca usava AutoStartKbps — que virava
    /// configuração morta. E como o agente faz SetMode(auto, c), esse 8000 virava
    /// o teto inicial da escada automática por coincidência de valor (ambos são
    /// 8000 por padrão), mascarando o defeito: bastava um operador configurar
    /// AutoStartKbps != ManualDefaultKbps para o modo auto começar no lugar errado.
    /// A troca para auto em runtime já foi corrigida para enviar AutoStartKbps
    /// (RemoteSessionCommandHandlers.cs); este método fecha a outra metade, para que
    /// "entrar na sessão em auto" e "trocar para auto durante a sessão" partam do
    /// mesmo teto.
    /// </summary>
    internal static int ResolveStartCeilingKbps(RemoteAccessBandwidthOptions bandwidth)
    {
        var mode = (bandwidth.DefaultMode ?? string.Empty).Trim().ToLowerInvariant();

        // ServerMaxKbps é o teto ABSOLUTO do servidor (qualquer modo): o valor
        // publicado já sai capado, para o backend ser autoritativo em vez de
        // depender só do applyServerCap do agente.
        return mode switch
        {
            "hd" => ClampToServerMax(0, bandwidth.ServerMaxKbps),
            "manual" => ClampToServerMax(bandwidth.ManualDefaultKbps, bandwidth.ServerMaxKbps),
            _ => ClampToServerMax(
                    Math.Clamp(bandwidth.AutoStartKbps, bandwidth.AutoMinKbps, Math.Max(bandwidth.AutoMinKbps, bandwidth.AutoMaxKbps)),
                    bandwidth.ServerMaxKbps)
        };
    }

    /// <summary>Aplica o teto absoluto do servidor (0 = sem restrição).</summary>
    private static int ClampToServerMax(int value, int serverMaxKbps)
        => serverMaxKbps > 0 && value > serverMaxKbps ? serverMaxKbps : value;

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
        RemoteAccessBandwidthOptions? bandwidth = null,
        CancellationToken ct = default)
    {
        // Política de TETO DE BANDA (kbps) aplicada no start. Vem da
        // configuração do servidor; o viewer pode sobrepor em runtime pelo
        // canal .control (ação "bandwidth"). Sem bloco, o agent usa o default
        // dele (sem teto), preservando o comportamento anterior.
        object? bandwidthBlock = bandwidth is null ? null : new
        {
            mode = bandwidth.DefaultMode,
            ceilingKbps = ResolveStartCeilingKbps(bandwidth),
            autoMinKbps = bandwidth.AutoMinKbps,
            autoMaxKbps = bandwidth.AutoMaxKbps,
            autoStepKbps = bandwidth.AutoStepKbps,
            serverMaxKbps = bandwidth.ServerMaxKbps
        };

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
            natsSubject,
            bandwidth = bandwidthBlock
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
        bool auto = false,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "quality",
            sessionId,
            quality = quality.ToString().ToLowerInvariant(),
            codec = codec?.ToString().ToLowerInvariant(),
            imageQuality,
            maxFps,
            auto
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
