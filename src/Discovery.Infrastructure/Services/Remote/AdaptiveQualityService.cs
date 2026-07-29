using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote;

/// <summary>
/// Background service que ajusta automaticamente a qualidade do stream com base
/// em métricas de rede (RTT, jitter, bandwidth). Sobe qualidade quando a rede está
/// boa e reduz quando está ruim, com histerese para evitar oscilações.
/// </summary>
public sealed class AdaptiveQualityService : BackgroundService
{
    private readonly SessionMetricsStore _metricsStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<AdaptiveQualityService> _logger;

    // Estado por sessão para histerese
    private readonly Dictionary<Guid, AdaptiveState> _states = new();

    public AdaptiveQualityService(
        SessionMetricsStore metricsStore,
        IServiceScopeFactory scopeFactory,
        IOptions<RemoteAccessOptions> options,
        ILogger<AdaptiveQualityService> logger)
    {
        _metricsStore = metricsStore;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Quality.AdaptiveEnabled)
        {
            _logger.LogInformation("AdaptiveQualityService: adaptive quality disabled in config");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Quality.AdaptiveIntervalSeconds));
        _logger.LogInformation("AdaptiveQualityService started (interval={Interval}s, hysteresis={Hysteresis}s)",
            interval.TotalSeconds, _options.Quality.AdaptiveHysteresisSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await EvaluateAndAdjustAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AdaptiveQualityService evaluation failed");
            }
        }
    }

    private async Task EvaluateAndAdjustAsync(CancellationToken ct)
    {
        var thresholds = _options.Quality.Thresholds;
        var hysteresis = TimeSpan.FromSeconds(_options.Quality.AdaptiveHysteresisSeconds);

        // Purga métricas stale (> 60s sem update)
        _metricsStore.PurgeStale(TimeSpan.FromSeconds(60));

        // Itera APENAS sobre sessões em modo Auto
        var autoSessions = _metricsStore.GetAutoModeSessions();
        foreach (var sessionId in autoSessions)
        {
            var metrics = _metricsStore.GetSnapshot(sessionId);
            if (metrics is null || metrics.SampleCount < 3) continue; // precisa de amostras suficientes

            var emaRtt = metrics.EmaRttMs;
            var emaBw = metrics.EmaBandwidthKbps;

            var state = GetOrCreateState(sessionId);

            // Determina qualidade alvo baseado nas métricas
            var targetQuality = DetermineTargetQuality(emaRtt, emaBw, state.CurrentQuality, thresholds);

            if (targetQuality == state.CurrentQuality)
            {
                // Sem mudança necessária — reseta tentativa de subida
                state.LastUpgradeAttempt = DateTime.MinValue;
                continue;
            }

            var now = DateTime.UtcNow;

            if (targetQuality > state.CurrentQuality)
            {
                // Quer SUBIR qualidade — precisa de histerese
                if (now - state.LastUpgradeAttempt < hysteresis)
                    continue; // ainda não passou o tempo mínimo
                state.LastUpgradeAttempt = now;
            }

            // Aplica a mudança
            _logger.LogInformation(
                "AdaptiveQuality: session {SessionId} quality {From}→{To} (RTT={Rtt:F0}ms, BW={Bw:F0}kbps)",
                sessionId, state.CurrentQuality, targetQuality, emaRtt, emaBw);

            await ApplyQualityChangeAsync(sessionId, targetQuality, ct);
            state.CurrentQuality = targetQuality;
        }
    }

    private static QualityProfile DetermineTargetQuality(
        double avgRttMs, double avgBandwidthKbps, QualityProfile current, AdaptiveThresholds t)
    {
        // Lógica de decisão:
        // - Se RTT alto OU bandwidth baixa → reduz
        // - Se RTT baixo E bandwidth alta → aumenta
        // - Caso contrário → mantém

        if (avgRttMs > t.HighLatencyMs || (avgBandwidthKbps > 0 && avgBandwidthKbps < t.LowBandwidthKbps))
        {
            // Condições ruins: reduz qualidade
            return current switch
            {
                QualityProfile.Ultra => QualityProfile.High,
                QualityProfile.High => QualityProfile.Medium,
                QualityProfile.Medium => QualityProfile.Low,
                QualityProfile.Low => QualityProfile.UltraLow,
                QualityProfile.UltraLow => QualityProfile.UltraLow,
                _ => QualityProfile.Medium
            };
        }

        if (avgRttMs < t.LowLatencyMs && (avgBandwidthKbps == 0 || avgBandwidthKbps > t.HighBandwidthKbps))
        {
            // Condições boas: aumenta qualidade
            return current switch
            {
                QualityProfile.UltraLow => QualityProfile.Low,
                QualityProfile.Low => QualityProfile.Medium,
                QualityProfile.Medium => QualityProfile.High,
                QualityProfile.High => QualityProfile.Ultra,
                QualityProfile.Ultra => QualityProfile.Ultra,
                _ => QualityProfile.High
            };
        }

        return current; // mantém
    }

    private async Task ApplyQualityChangeAsync(Guid sessionId, QualityProfile quality, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<IRemoteSessionManager>();
        var commandRepo = scope.ServiceProvider.GetRequiredService<ICommandRepository>();
        var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();

        var session = await sessionManager.GetRawSessionAsync(sessionId, ct);
        if (session is null || session.Status != "active") return;

        try
        {
            var (defaultFps, _, jpegQ, _) = QualityProfileMapping.GetParameters(quality);

            await sessionManager.UpdateQualityAsync(sessionId, quality, session.Codec, jpegQ, defaultFps, ct);

            // Dispara comando quality para o agent com imageQuality e maxFps separados
            var payload = JsonSerializer.Serialize(new
            {
                action = "quality",
                sessionId,
                quality = quality.ToString().ToLowerInvariant(),
                codec = session.Codec.ToString().ToLowerInvariant(),
                imageQuality = jpegQ,
                maxFps = defaultFps
            });

            var wireCommandType = CommandTypeWireMapper.ToWireValue(CommandType.RemoteSessionQuality);
            var command = new AgentCommand
            {
                AgentId = session.AgentId,
                CommandType = CommandType.RemoteSessionQuality,
                Payload = payload
            };

            var created = await commandRepo.CreateAsync(command);
            if (messaging.IsConnected)
            {
                await messaging.SendCommandAsync(session.AgentId, created.Id, wireCommandType, payload);
                created.SentAt = DateTime.UtcNow;
                await commandRepo.UpdateStatusAsync(created.Id, CommandStatus.Sent, null, null, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AdaptiveQuality failed to apply change for session {SessionId}", sessionId);
        }
    }

    private AdaptiveState GetOrCreateState(Guid sessionId)
    {
        if (!_states.TryGetValue(sessionId, out var state))
        {
            state = new AdaptiveState();
            _states[sessionId] = state;
        }
        return state;
    }

    private sealed class AdaptiveState
    {
        public QualityProfile CurrentQuality { get; set; } = QualityProfile.High;
        public DateTime LastUpgradeAttempt { get; set; } = DateTime.MinValue;
    }
}
