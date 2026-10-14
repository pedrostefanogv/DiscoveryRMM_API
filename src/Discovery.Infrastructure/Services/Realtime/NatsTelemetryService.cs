using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace Discovery.Infrastructure.Services.Realtime;

/// <summary>
/// Snapshot de telemetria do NATS mantido em memoria. Todos os campos sao
/// anulaveis: ausencia de dado significa "nao verificado", nunca "falso".
/// </summary>
public sealed record NatsTelemetrySnapshot(
    bool? TcpReachable,
    int? TcpLatencyMs,
    DateTime? TcpCheckedAtUtc,
    int? RttMs,
    DateTime? RttCheckedAtUtc,
    string? LastError);

/// <summary>
/// Coleta telemetria do NATS (probe TCP + RTT) com cache e single-flight.
///
/// Regras anti-sobrecarga:
/// - O probe TCP e o PING acontecem no maximo uma vez por janela de cache
///   (Nats:Telemetry:CacheSeconds, padrao 30s), nunca por request.
/// - Single-flight: N usuarios/abas simultaneos disparam UM probe.
/// - Timeout curto e resultado estruturado: falha nao derruba a request.
/// </summary>
public sealed class NatsTelemetryService
{
    private const int DefaultCacheSeconds = 30;
    private const int DefaultTcpTimeoutSeconds = 2;

    private readonly NatsConnection _connection;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NatsTelemetryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NatsTelemetrySnapshot _snapshot =
        new(null, null, null, null, null, null);
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public NatsTelemetryService(
        NatsConnection connection,
        IConfiguration configuration,
        ILogger<NatsTelemetryService> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Ultimo snapshot conhecido (pode estar desatualizado; nunca faz I/O).</summary>
    public NatsTelemetrySnapshot Current => Volatile.Read(ref _snapshot);

    public async Task<NatsTelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _expiresAtUtc)
            return Current;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check: outra request pode ter atualizado enquanto esperavamos.
            if (DateTime.UtcNow < _expiresAtUtc)
                return Current;

            var fresh = await RefreshAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, fresh);
            _expiresAtUtc = DateTime.UtcNow.AddSeconds(GetCacheSeconds());
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forca uma coleta (usado pelo refresh em background).</summary>
    public async Task RefreshNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fresh = await RefreshAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, fresh);
            _expiresAtUtc = DateTime.UtcNow.AddSeconds(GetCacheSeconds());
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<NatsTelemetrySnapshot> RefreshAsync(CancellationToken ct)
    {
        var tcp = await ProbeTcpAsync(ct).ConfigureAwait(false);
        var rtt = await ProbeRttAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Erro so permanece quando ha falha atual; sucesso limpa o estado anterior.
        var error = tcp.Error ?? rtt.Error;

        return new NatsTelemetrySnapshot(
            TcpReachable: tcp.Reachable,
            TcpLatencyMs: tcp.LatencyMs,
            TcpCheckedAtUtc: now,
            RttMs: rtt.RttMs,
            RttCheckedAtUtc: rtt.RttMs is null ? null : now,
            LastError: error);
    }

    private async Task<(bool? Reachable, int? LatencyMs, string? Error)> ProbeTcpAsync(CancellationToken ct)
    {
        var endpoint = ParseEndpoint(_connection.Opts.Url);
        if (endpoint is null)
            return (null, null, "NATS URL ausente ou invalida");

        var timeoutSeconds = Math.Clamp(
            _configuration.GetValue<int?>("Nats:Telemetry:TcpProbeTimeoutSeconds") ?? DefaultTcpTimeoutSeconds,
            1,
            10);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await client.ConnectAsync(endpoint.Value.Host, endpoint.Value.Port, timeout.Token)
                .ConfigureAwait(false);

            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Probe TCP do NATS falhou para {Endpoint}", endpoint.Value);
            return (false, (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private async Task<(int? RttMs, string? Error)> ProbeRttAsync(CancellationToken ct)
    {
        if (_connection.ConnectionState != NatsConnectionState.Open)
            return (null, null);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var sw = Stopwatch.StartNew();
            await _connection.PingAsync(timeout.Token).ConfigureAwait(false);
            sw.Stop();
            return ((int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PING do NATS falhou");
            return (null, ex.Message);
        }
    }

    private int GetCacheSeconds() => Math.Clamp(
        _configuration.GetValue<int?>("Nats:Telemetry:CacheSeconds") ?? DefaultCacheSeconds,
        5,
        600);

    /// <summary>
    /// Extrai host/porta de uma URL NATS (nats://, tls://, ws://, wss://) para o
    /// probe TCP. Retorna null quando a URL nao e parseavel.
    /// </summary>
    internal static (string Host, int Port)? ParseEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        if (!uri.IsDefaultPort)
            return (uri.Host, uri.Port);

        var port = uri.Scheme.ToLowerInvariant() switch
        {
            "nats" => 4222,
            "tls" => 4222,
            "ws" => 80,
            "wss" => 443,
            "http" => 80,
            "https" => 443,
            _ => 4222
        };

        return (uri.Host, port);
    }
}

/// <summary>
/// Mantem a telemetria NATS quente (fora do caminho da request do dashboard).
/// </summary>
public sealed class NatsTelemetryRefreshService : BackgroundService
{
    private readonly NatsTelemetryService _telemetry;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NatsTelemetryRefreshService> _logger;

    public NatsTelemetryRefreshService(
        NatsTelemetryService telemetry,
        IConfiguration configuration,
        ILogger<NatsTelemetryRefreshService> logger)
    {
        _telemetry = telemetry;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("Nats:Telemetry:RefreshSeconds") ?? 60,
            15,
            3600);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _telemetry.RefreshNowAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Refresh de telemetria do NATS falhou");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
