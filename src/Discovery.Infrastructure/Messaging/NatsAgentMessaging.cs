using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Discovery.Infrastructure.Messaging;

/// <summary>
/// Implementação NATS para comunicação em tempo real com agents.
/// Subjects:
///   tenant.{clientId}.site.{siteId}.agent.{agentId}.command   → Servidor → Agent (enviar comando)
///   tenant.{clientId}.site.{siteId}.agents.command            → Servidor → Agents do site (fan-out)
///   tenant.{clientId}.agents.command                          → Servidor → Agents do cliente (fan-out)
///   tenant.global.agents.command                              → Servidor → Agents globais (fan-out)
///   tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat → Agent → Servidor (heartbeat)
///   tenant.{clientId}.site.{siteId}.agent.{agentId}.result    → Agent → Servidor (resultado de comando)
///   tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware  → Agent → Servidor (hw report)
///   tenant.{clientId}.dashboard.events e tenant.{clientId}.site.{siteId}.dashboard.events → Servidor → Dashboard
///   tenant.{clientId}.site.{siteId}.p2p.discovery → Servidor → Agents (snapshot de descoberta P2P)
/// </summary>
public class NatsAgentMessaging : IAgentMessaging, IAsyncDisposable
{
    private readonly NatsConnection _connection;
    private readonly IAgentRepository _agentRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IAgentAuthService _agentAuthService;
    private readonly IHeartbeatCacheService _heartbeatCache;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly DashboardEventContractNormalizer _contractNormalizer;
    private readonly ILogger<NatsAgentMessaging> _logger;
    private readonly IOptionsMonitor<NatsGlobalPongOptions> _globalPongOptions;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    // Cache local de autorizacao de agent (TTL 30s) para reduzir DB/Redis hit por heartbeat.
    private readonly ConcurrentDictionary<Guid, (bool Authorized, DateTime ExpiresAt)> _authCache = new();
    private static readonly TimeSpan AuthCacheTtl = TimeSpan.FromSeconds(30);

    public NatsAgentMessaging(
        NatsConnection connection,
        IAgentRepository agentRepo,
        ICommandRepository commandRepo,
        ISiteRepository siteRepo,
        IAgentAuthService agentAuthService,
        IHeartbeatCacheService heartbeatCache,
        IAgentHardwareRepository hardwareRepo,
        DashboardEventContractNormalizer contractNormalizer,
        IOptionsMonitor<NatsGlobalPongOptions> globalPongOptions,
        ILogger<NatsAgentMessaging> logger)
    {
        _connection = connection;
        _agentRepo = agentRepo;
        _commandRepo = commandRepo;
        _siteRepo = siteRepo;
        _agentAuthService = agentAuthService;
        _heartbeatCache = heartbeatCache;
        _hardwareRepo = hardwareRepo;
        _contractNormalizer = contractNormalizer;
        _globalPongOptions = globalPongOptions;
        _logger = logger;
    }

    /// <summary>
    /// Verifica que o agent possui ao menos um token ativo.
    /// Defesa contra spoofing via NATS: mensagens de AgentIds sem token válido são descartadas.
    /// </summary>
    private async Task<bool> IsAgentAuthorizedAsync(Guid agentId)
    {
        // Cache local (TTL 30s) para evitar DB/Redis hit por heartbeat em alta carga.
        var now = DateTime.UtcNow;
        if (_authCache.TryGetValue(agentId, out var cached) && now < cached.ExpiresAt)
            return cached.Authorized;

        var tokens = await _agentAuthService.GetTokensByAgentIdAsync(agentId);
        var authorized = tokens.Any(t => t.IsValid);

        _authCache[agentId] = (authorized, now.Add(AuthCacheTtl));

        // Limpeza periodica do cache (remove entradas expiradas a cada ~100 acessos).
        if (_authCache.Count > 10_000)
        {
            foreach (var kv in _authCache.Where(kv => now >= kv.Value.ExpiresAt).ToList())
                _authCache.TryRemove(kv.Key, out _);
        }

        return authorized;
    }

    public bool IsConnected => _connection.ConnectionState == NatsConnectionState.Open;

    public async Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload)
    {
        var subject = await BuildAgentSubjectAsync(agentId, "command");
        var message = JsonSerializer.Serialize(new
        {
            CommandId = commandId,
            CommandType = commandType,
            Payload = payload
        }, JsonOptions);

        await _connection.PublishAsync(subject, message);
        _logger.LogDebug("Command {CommandId} sent to agent {AgentId} via NATS", commandId, agentId);
    }

    public async Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var subject = NatsSubjectBuilder.SiteAgentsCommandSubject(clientId, siteId);
        var normalized = envelope with
        {
            TargetScope = "site",
            TargetClientId = clientId,
            TargetSiteId = siteId
        };

        await PublishFanoutCommandAsync(subject, normalized, cancellationToken);
    }

    public async Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var subject = NatsSubjectBuilder.ClientAgentsCommandSubject(clientId);
        var normalized = envelope with
        {
            TargetScope = "client",
            TargetClientId = clientId,
            TargetSiteId = null
        };

        await PublishFanoutCommandAsync(subject, normalized, cancellationToken);
    }

    public async Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var subject = NatsSubjectBuilder.GlobalAgentsCommandSubject();
        var normalized = envelope with
        {
            TargetScope = "global",
            TargetClientId = null,
            TargetSiteId = null
        };

        await PublishFanoutCommandAsync(subject, normalized, cancellationToken);
    }

    public async Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default)
    {
        // Cancelamento é respeitado antes da operação; após início do publish, ignora para
        // evitar estado inconsistente (mensagem pode já ter sido parcialmente enviada).
        if (cancellationToken.IsCancellationRequested)
            return;

        var payload = JsonSerializer.Serialize(message, JsonOptions);

        // Aplica validacao/normalizacao de contrato antes de cada publish.
        var source = $"nats:{NatsSubjectBuilder.DashboardSubject(message.ClientId, message.SiteId)}";
        if (_contractNormalizer.TryNormalize(payload, source, out var normalized))
        {
            // Se a normalizacao foi bem-sucedida, usa o payload normalizado.
            var normalizedPayload = JsonSerializer.Serialize(new
            {
                eventType = normalized!.EventType,
                data = normalized.Data,
                timestampUtc = normalized.TimestampUtc.ToString("O"),
                clientId = normalized.ClientId,
                siteId = normalized.SiteId,
            }, JsonOptions);
            var subject = NatsSubjectBuilder.DashboardSubject(normalized.ClientId, normalized.SiteId);
            await _connection.PublishAsync(subject, normalizedPayload);
        }
        else
        {
            // Normalizacao falhou — publica o payload original (comportamento degracioso).
            // Violacoes ja foram logadas pelo normalizador.
            var subject = NatsSubjectBuilder.DashboardSubject(message.ClientId, message.SiteId);
            await _connection.PublishAsync(subject, payload);
        }
    }

    public async Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var subject = await BuildAgentSubjectAsync(agentId, "sync.ping");
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

        // Subscrever heartbeats: tenant.*.site.*.agent.*.heartbeat
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
            await foreach (var msg in _connection.SubscribeAsync<string>("tenant.*.site.*.agent.*.heartbeat", cancellationToken: cancellationToken))
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
                            var heartbeat = JsonSerializer.Deserialize<AgentHeartbeat>(msg.Data ?? "", JsonOptions);
                            if (heartbeat is null)
                            {
                                _logger.LogWarning("NATS: heartbeat inválido (deserialização falhou) de {Subject}", msg.Subject);
                                continue;
                            }

                            // Garante que o AgentId do subject é usado (não confia no payload)
                            heartbeat = heartbeat with { AgentId = agentId.Value };

                            // Cache no Redis com métricas (write-behind evita escrita direta no PostgreSQL)
                            var wasTransition = await _heartbeatCache.SetHeartbeatAsync(heartbeat, AgentStatus.Online);

                            _logger.LogDebug("Heartbeat processed from agent {AgentId} (IP: {IpAddress}, CPU:{Cpu}%, Mem:{Mem}%, Disk:{Disk}%, P2P:{P2p})",
                                agentId.Value, heartbeat.IpAddress,
                                heartbeat.CpuPercent, heartbeat.MemoryPercent,
                                heartbeat.DiskPercent, heartbeat.P2pPeers);

                            // Se houve transição Offline→Online e o agent tem PeerId, publica peer.online
                            if (wasTransition && !string.IsNullOrWhiteSpace(heartbeat.PeerId))
                            {
                                try
                                {
                                    var clientId = heartbeat.ClientId ?? Guid.Empty;
                                    // Sequence monotonicamente crescente por cliente via timestamp Unix ms.
                                    // Idealmente usaria Redis INCR, mas para manter zero dependencia extra,
                                    // usamos Unix ms (crescente, nao garantidamente gap-free mas suficiente para dedupe).
                                    var sequence = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % int.MaxValue);

                                    var onlineEvent = new P2pPeerOnlineEvent
                                    {
                                        EventType = "peer.online",
                                        ClientId = clientId,
                                        SiteId = heartbeat.SiteId,
                                        AgentId = agentId.Value,
                                        PeerId = heartbeat.PeerId!,
                                        Addrs = heartbeat.Addrs ?? Array.Empty<string>(),
                                        Port = heartbeat.Port ?? 0,
                                        GeneratedAtUtc = DateTime.UtcNow,
                                        Sequence = sequence,
                                    };

                                    var eventJson = JsonSerializer.Serialize(onlineEvent, JsonOptions);
                                    var subject = NatsSubjectBuilder.P2pClientEventsSubject(clientId);
                                    await _connection.PublishAsync(subject, eventJson);
                                    _logger.LogInformation("Published peer.online for agent {AgentId} (PeerId: {PeerId}, Sequence: {Sequence})", agentId.Value, heartbeat.PeerId, sequence);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to publish peer.online for agent {AgentId}", agentId.Value);
                                }
                            }

                            // Propaga heartbeat completo para dashboards via NATS dashboard.events.
                            // Usa ClientId/SiteId do heartbeat (enviado pelo agent) em vez de DB lookup.
                            var eventPayload = new
                            {
                                AgentId = agentId.Value,
                                Status = "Online",
                                ClientId = heartbeat.ClientId,
                                SiteId = heartbeat.SiteId,
                                heartbeat.IpAddress,
                                heartbeat.Hostname,
                                heartbeat.AgentVersion,
                                heartbeat.TimestampUtc,
                                heartbeat.CpuPercent,
                                heartbeat.MemoryPercent,
                                heartbeat.MemoryTotalGb,
                                heartbeat.MemoryUsedGb,
                                heartbeat.DiskPercent,
                                heartbeat.DiskTotalGb,
                                heartbeat.DiskUsedGb,
                                heartbeat.P2pPeers,
                                heartbeat.UptimeSeconds,
                                heartbeat.ProcessCount
                            };

                            var dashboardMessage = DashboardEventMessage.Create(
                                "AgentHeartbeat", eventPayload,
                                heartbeat.ClientId, heartbeat.SiteId);

                            // Valida conformidade do contrato antes de publicar.
                            if (!IsValidHeartbeatPayload(heartbeat))
                                _logger.LogWarning("[CONTRACT_VIOLATION] component=Server field=AgentHeartbeat reason=InvalidPayload agentId={AgentId} subject={Subject}", agentId.Value, msg.Subject);

                            await PublishDashboardEventAsync(dashboardMessage, CancellationToken.None);
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

        // Subscrever resultados: tenant.*.site.*.agent.*.result
        var resultTask = Task.Run(async () =>
        {
            try
            {
            await foreach (var msg in _connection.SubscribeAsync<string>("tenant.*.site.*.agent.*.result", cancellationToken: cancellationToken))
                {
                    try
                    {
                        var agentId = ExtractAgentId(msg.Subject);
                        if (!agentId.HasValue)
                        {
                            _logger.LogWarning("[CONTRACT_VIOLATION] component=Server field=subject reason=MissingAgentId subject={Subject}", msg.Subject);
                            continue;
                        }

                        if (!await IsAgentAuthorizedAsync(agentId.Value))
                        {
                            _logger.LogWarning("NATS: command result rejeitado de agent sem token válido (possível spoofing) — AgentId={AgentId}, Subject={Subject}", agentId.Value, msg.Subject);
                            continue;
                        }
                        var result = JsonSerializer.Deserialize<CommandResultMessage>(msg.Data ?? "", JsonOptions);
                        if (result is null)
                        {
                            _logger.LogWarning("[CONTRACT_VIOLATION] component=Server field=CommandResult reason=DeserializationFailed agentId={AgentId} subject={Subject}", agentId.Value, msg.Subject);
                            continue;
                        }

                        var status = result.ExitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed;
                        await _commandRepo.UpdateStatusAsync(result.CommandId, status, result.Output, result.ExitCode, result.ErrorMessage);

                        // Publica dashboard event com dispatchId para agregacao de campanha (contrato secao 2.3).
                        var resultData = new
                        {
                            CommandId = result.CommandId,
                            result.ExitCode,
                            result.Output,
                            result.ErrorMessage,
                            DispatchId = result.DispatchId,
                        };
                        await PublishDashboardEventForAgentAsync(
                            agentId,
                            "CommandCompleted",
                            resultData,
                            cancellationToken);
                        _logger.LogDebug("Command result processed: {CommandId} - Exit Code: {ExitCode} (DispatchId: {DispatchId})", result.CommandId, result.ExitCode, result.DispatchId);
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

        // Subscrever reports de hardware: tenant.*.site.*.agent.*.hardware
        var hardwareTask = Task.Run(async () =>
        {
            try
            {
            await foreach (var msg in _connection.SubscribeAsync<string>("tenant.*.site.*.agent.*.hardware", cancellationToken: cancellationToken))
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

                            // Persiste inventario de hardware (contrato: hardware report deve ser persistido).
                            try
                            {
                                var hwData = msg.Data ?? "";
                                if (!string.IsNullOrWhiteSpace(hwData))
                                {
                                    using var doc = System.Text.Json.JsonDocument.Parse(hwData);
                                    var root = doc.RootElement;

                                    var hardwareInfo = new AgentHardwareInfo
                                    {
                                        AgentId = agentId.Value,
                                        InventoryCollectedAt = DateTime.UtcNow,
                                    };

                                    // Extrai campos do payload do agent (Go) — nomes camelCase.
                                    if (root.TryGetProperty("hostname", out var h)) hardwareInfo.Model = h.GetString();
                                    if (root.TryGetProperty("operatingSystem", out var os)) hardwareInfo.BiosVersion = os.GetString();
                                    if (root.TryGetProperty("osVersion", out var osv)) _ = osv.GetString(); // guarda para log

                                    if (root.TryGetProperty("hardware", out var hw) && hw.ValueKind == JsonValueKind.Object)
                                    {
                                        if (hw.TryGetProperty("manufacturer", out var mfr)) hardwareInfo.Manufacturer = mfr.GetString();
                                        if (hw.TryGetProperty("model", out var mdl)) hardwareInfo.Model = mdl.GetString();
                                        if (hw.TryGetProperty("serialNumber", out var sn)) hardwareInfo.SerialNumber = sn.GetString();
                                        if (hw.TryGetProperty("motherboardManufacturer", out var mb)) hardwareInfo.MotherboardManufacturer = mb.GetString();
                                        if (hw.TryGetProperty("motherboardModel", out var mbMdl)) hardwareInfo.MotherboardModel = mbMdl.GetString();
                                        if (hw.TryGetProperty("motherboardSerialNumber", out var mbSn)) hardwareInfo.MotherboardSerialNumber = mbSn.GetString();
                                        if (hw.TryGetProperty("processor", out var cpu)) hardwareInfo.Processor = cpu.GetString();
                                        if (hw.TryGetProperty("processorCores", out var pc) && pc.TryGetInt32(out var pcVal)) hardwareInfo.ProcessorCores = pcVal;
                                        if (hw.TryGetProperty("processorThreads", out var pt) && pt.TryGetInt32(out var ptVal)) hardwareInfo.ProcessorThreads = ptVal;
                                        if (hw.TryGetProperty("processorArchitecture", out var arch)) hardwareInfo.ProcessorArchitecture = arch.GetString();
                                        if (hw.TryGetProperty("processorTdpWatts", out var tdp) && tdp.TryGetInt32(out var tdpVal)) hardwareInfo.ProcessorTdpWatts = tdpVal;
                                        if (hw.TryGetProperty("processorSocket", out var sock)) hardwareInfo.ProcessorSocket = sock.GetString();
                                        if (hw.TryGetProperty("processorFrequencyGhz", out var freq) && freq.TryGetDecimal(out var freqVal)) hardwareInfo.ProcessorFrequencyGhz = freqVal;
                                        if (hw.TryGetProperty("processorReleaseDate", out var prd)) hardwareInfo.ProcessorReleaseDate = prd.GetString();
                                        if (hw.TryGetProperty("totalMemoryBytes", out var mem) && mem.TryGetInt64(out var memVal)) hardwareInfo.TotalMemoryBytes = memVal;
                                        if (hw.TryGetProperty("gpuModel", out var gpu)) hardwareInfo.GpuModel = gpu.GetString();
                                        if (hw.TryGetProperty("gpuMemoryBytes", out var gpuMem) && gpuMem.TryGetInt64(out var gpuMemVal)) hardwareInfo.GpuMemoryBytes = gpuMemVal;
                                        if (hw.TryGetProperty("gpuDriverVersion", out var gpuDrv)) hardwareInfo.GpuDriverVersion = gpuDrv.GetString();
                                        if (hw.TryGetProperty("totalDisksCount", out var dsk) && dsk.TryGetInt32(out var dskVal)) hardwareInfo.TotalDisksCount = dskVal;
                                        if (hw.TryGetProperty("biosVersion", out var biosV)) hardwareInfo.BiosVersion = biosV.GetString();
                                        if (hw.TryGetProperty("biosManufacturer", out var biosMfr)) hardwareInfo.BiosManufacturer = biosMfr.GetString();
                                        if (hw.TryGetProperty("biosDate", out var biosDt)) hardwareInfo.BiosDate = biosDt.GetString();
                                        if (hw.TryGetProperty("biosSerialNumber", out var biosSn)) hardwareInfo.BiosSerialNumber = biosSn.GetString();
                                    }

                                    if (root.TryGetProperty("machineScore", out var ms) && ms.TryGetInt32(out var msVal))
                                        hardwareInfo.MachineScore = msVal;

                                    if (root.TryGetProperty("inventoryRaw", out var inv))
                                        hardwareInfo.InventoryRaw = inv.GetString();

                                    await _hardwareRepo.UpsertAsync(hardwareInfo);
                                    _logger.LogDebug("Hardware report persisted for agent {AgentId} (model={Model}, cpu={Cpu})",
                                        agentId.Value, hardwareInfo.Model, hardwareInfo.Processor);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to persist hardware report for agent {AgentId}", agentId.Value);
                            }

                            // Propaga para dashboards via NATS dashboard.events (mesmo payload original como data)
                            var hwEventData = msg.Data is not null && msg.Data.Length > 0
                                ? System.Text.Json.JsonSerializer.Deserialize<object>(msg.Data, JsonOptions)
                                : new { AgentId = agentId.Value };
                            await PublishDashboardEventForAgentAsync(
                                agentId.Value,
                                "AgentHardwareReported",
                                hwEventData ?? new { AgentId = agentId.Value },
                                CancellationToken.None);
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

        // Publicador periodico de pong global (independente do fluxo de heartbeat de agents).
        var globalPongTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Global pong periodic publisher started.");

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await PublishServerPongAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Global pong periodic publish failed; retrying on next interval.");
                    }

                    var interval = ResolveGlobalPongInterval();
                    await Task.Delay(interval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Global pong periodic publisher cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in global pong periodic publisher");
            }
        }, cancellationToken);

        _logger.LogInformation("NATS agent message subscriptions started successfully");

        // Mantém o método ativo enquanto as subscriptions estiverem vivas.
        await Task.WhenAll(heartbeatTask, resultTask, hardwareTask, globalPongTask);
    }

    private static Guid? ExtractAgentId(string subject)
    {
        // subject format: tenant.{clientId}.site.{siteId}.agent.{agentId}.{messageType}
        var parts = subject.Split('.');
        var agentIndex = Array.FindIndex(parts, part => string.Equals(part, "agent", StringComparison.OrdinalIgnoreCase));
        if (agentIndex >= 0 && parts.Length > agentIndex + 1 && Guid.TryParse(parts[agentIndex + 1], out var id))
            return id;
        return null;
    }

    private async Task<string> BuildAgentSubjectAsync(Guid agentId, string messageType)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found.");
        var site = await _siteRepo.GetByIdAsync(agent.SiteId)
            ?? throw new InvalidOperationException($"Site '{agent.SiteId}' not found for agent.");

        return NatsSubjectBuilder.AgentSubject(site.ClientId, site.Id, agentId, messageType);
    }

    public async ValueTask DisposeAsync()
    {
        // NatsConnection é singleton no DI e não deve ser descartada por este serviço scoped.
        await ValueTask.CompletedTask;
    }

    private async Task PublishFanoutCommandAsync(string subject, CommandDispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(envelope, JsonOptions);

        var headers = new NatsHeaders
        {
            ["Nats-Msg-Id"] = envelope.IdempotencyKey,
        };

        await _connection.PublishAsync(subject, payload, headers: headers, cancellationToken: cancellationToken);
        _logger.LogInformation(
            "Fan-out command dispatch {DispatchId} published to {Subject} (scope={TargetScope})",
            envelope.DispatchId,
            subject,
            envelope.TargetScope);
    }

    private async Task PublishServerPongAsync(CancellationToken cancellationToken)
    {
        var serverOverloaded = ResolveServerOverloadedFlag();

        var pong = new GlobalPongMessage
        {
            ServerTimeUtc = DateTime.UtcNow,
            ServerOverloaded = serverOverloaded,
        };

        var subject = NatsSubjectBuilder.ServerPongSubject();
        var payload = JsonSerializer.Serialize(pong, JsonOptions);
        await _connection.PublishAsync(subject, payload, cancellationToken: cancellationToken);

        _logger.LogDebug("Global pong published to {Subject} (serverOverloaded={ServerOverloaded})", subject, pong.ServerOverloaded);
    }

    private TimeSpan ResolveGlobalPongInterval()
    {
        var configuredSeconds = _globalPongOptions.CurrentValue.PublishIntervalSeconds;
        if (configuredSeconds <= 0)
        {
            _logger.LogWarning(
                "Invalid Nats:GlobalPong:PublishIntervalSeconds value '{Seconds}'. Falling back to 60 seconds.",
                configuredSeconds);
            configuredSeconds = 60;
        }

        return TimeSpan.FromSeconds(configuredSeconds);
    }

    private bool? ResolveServerOverloadedFlag()
    {
        var options = _globalPongOptions.CurrentValue;
        var mode = options.OverloadMode?.Trim().ToLowerInvariant();

        return mode switch
        {
            "" or "disabled" => null,
            "forced" => options.ForcedValue,
            "auto" => EvaluateAutoOverload(options),
            _ => HandleUnknownOverloadMode(mode),
        };
    }

    private bool EvaluateAutoOverload(NatsGlobalPongOptions options)
    {
        var workerThreshold = Math.Clamp(options.WorkerThreadUsageThresholdPercent, 0, 100);
        var memoryThreshold = Math.Clamp(options.ManagedMemoryUsageThresholdPercent, 0, 100);

        ThreadPool.GetAvailableThreads(out var availableWorkerThreads, out _);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out _);

        var workerUsagePercent = maxWorkerThreads > 0
            ? (1d - ((double)availableWorkerThreads / maxWorkerThreads)) * 100d
            : 0d;
        var workerOverloaded = workerUsagePercent >= workerThreshold;

        var memoryUsagePercent = 0d;
        var memoryOverloaded = false;
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            var totalAvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes;
            if (totalAvailableMemoryBytes > 0)
            {
                var managedUsedBytes = GC.GetTotalMemory(forceFullCollection: false);
                memoryUsagePercent = (double)managedUsedBytes / totalAvailableMemoryBytes * 100d;
                memoryOverloaded = memoryUsagePercent >= memoryThreshold;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to evaluate managed memory usage for global pong overload flag.");
        }

        var overloaded = workerOverloaded || memoryOverloaded;

        _logger.LogDebug(
            "Global pong overload auto-evaluation: overloaded={Overloaded}, workerUsage={WorkerUsage:F1}% (threshold={WorkerThreshold:F1}%), memoryUsage={MemoryUsage:F1}% (threshold={MemoryThreshold:F1}%)",
            overloaded,
            workerUsagePercent,
            workerThreshold,
            memoryUsagePercent,
            memoryThreshold);

        return overloaded;
    }

    private bool? HandleUnknownOverloadMode(string? mode)
    {
        _logger.LogWarning(
            "Unknown Nats:GlobalPong:OverloadMode value '{Mode}'. Falling back to disabled mode (serverOverloaded omitted).",
            mode);
        return null;
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
            try
            {
                var agent = await _agentRepo.GetByIdAsync(agentId.Value);
                if (agent is not null)
                {
                    siteId = agent.SiteId;
                    var site = await _siteRepo.GetByIdAsync(agent.SiteId);
                    clientId = site?.ClientId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PublishDashboardEventForAgentAsync: erro ao resolver tenant do agent {AgentId} — propagando sem tenant.",
                    agentId.Value);
                // clientId e siteId permanecem null — evento propaga via subject de fallback.
            }
        }

        var message = DashboardEventMessage.Create(eventType, data, clientId, siteId);
        await PublishDashboardEventAsync(message, cancellationToken);
    }

    /// <summary>
    /// Valida que o payload de heartbeat contem os campos minimos esperados pelo contrato canonico.
    /// </summary>
    private static bool IsValidHeartbeatPayload(AgentHeartbeat heartbeat)
    {
        // Eventos com campos NaN ou negativos indicam bug no agent e sao reportados como violacao.
        if (heartbeat.CpuPercent.HasValue && (double.IsNaN(heartbeat.CpuPercent.Value) || heartbeat.CpuPercent.Value < 0))
            return false;
        if (heartbeat.MemoryPercent.HasValue && (double.IsNaN(heartbeat.MemoryPercent.Value) || heartbeat.MemoryPercent.Value < 0))
            return false;
        if (heartbeat.DiskPercent.HasValue && (double.IsNaN(heartbeat.DiskPercent.Value) || heartbeat.DiskPercent.Value < 0))
            return false;
        return true;
    }

    private record CommandResultMessage(Guid CommandId, int ExitCode, string? Output, string? ErrorMessage, Guid? DispatchId);
}
