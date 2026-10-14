using System.Diagnostics;
using System.Runtime.InteropServices;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Realtime.Queries;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services.Realtime;
using MediatR;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;

namespace Discovery.Infrastructure.Cqrs.Realtime;

public sealed class GetRealtimeStatusQueryHandler(
    NatsConnection natsConnection
) : IRequestHandler<GetRealtimeStatusQuery, Result<RealtimeStatusDto>>
{
    public Task<Result<RealtimeStatusDto>> Handle(GetRealtimeStatusQuery q, CancellationToken ct)
    {
        // Endpoint resumido/legado. Contagens de conexoes e fila nao tem fonte
        // barata aqui — o dashboard usa /api/v1/realtime/stats. Mantido apenas
        // com o estado real da conexao para nao devolver zeros enganosos.
        var connected = natsConnection.ConnectionState == NatsConnectionState.Open;
        return Task.FromResult(Result<RealtimeStatusDto>.Success(
            new RealtimeStatusDto(0, 0, connected ? DateTime.UtcNow : null)));
    }
}

public sealed class GetRealtimeStatsQueryHandler(
    NatsConnection natsConnection,
    IRedisService redis,
    DiscoveryDbContext db,
    NatsTelemetryService natsTelemetry,
    IHostEnvironment environment
) : IRequestHandler<GetRealtimeStatsQuery, Result<RealtimeStatsDto>>
{
    public async Task<Result<RealtimeStatsDto>> Handle(GetRealtimeStatsQuery q, CancellationToken ct)
    {
        var proc = Process.GetCurrentProcess();
        var startTime = proc.StartTime.ToUniversalTime();
        var uptime = DateTime.UtcNow - startTime;

        // ── Saúde da Plataforma ──────────────────────────────────────
        var natsConnected = natsConnection.ConnectionState == NatsConnectionState.Open;
        // A URL do NATS nao e exposta: pode conter hostname/porta internos e e
        // visivel para qualquer usuario com permissao de Dashboard.View.
        var natsConfigured = !string.IsNullOrWhiteSpace(natsConnection.Opts.Url);

        var redisConnected = redis.IsConnected;
        var redisPingMs = redisConnected ? await redis.PingAsync(ct).ConfigureAwait(false) : null;

        bool dbConnected;
        try { dbConnected = await db.Database.CanConnectAsync(ct).ConfigureAwait(false); }
        catch { dbConnected = false; }

        // Telemetria do NATS vem de snapshot cacheado (probe TCP + RTT) — nunca
        // faz I/O por request com o cache quente.
        var nats = await natsTelemetry.GetSnapshotAsync(ct).ConfigureAwait(false);

        // ── Métricas do Processo ─────────────────────────────────────
        var workingSetBytes = Environment.WorkingSet;
        var gcHeapBytes = GC.GetTotalMemory(forceFullCollection: false);

        ThreadPool.GetAvailableThreads(out var availableWorker, out var availableIo);
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);

        var stats = new RealtimeStatsDto(
            CheckedAtUtc: DateTime.UtcNow,
            Application: new
            {
                environment = environment.EnvironmentName,
                version = Environment.Version.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})",
                processId = Environment.ProcessId,
                machineName = Environment.MachineName,
                startedAtUtc = startTime,
                uptimeSeconds = (long)uptime.TotalSeconds,
                uptime = FormatUptime(uptime)
            },
            Realtime: new
            {
                natsConnected,
                natsConfigured,
                natsConnectionState = natsConnection.ConnectionState.ToString(),
                // Nullable de proposito: a UI trata null como "não verificado",
                // nunca como "fora do ar".
                natsTcpReachable = nats.TcpReachable,
                natsTcpLatencyMs = nats.TcpLatencyMs,
                natsTcpCheckedAtUtc = nats.TcpCheckedAtUtc,
                natsRttMs = nats.RttMs,
                natsRttCheckedAtUtc = nats.RttCheckedAtUtc,
                natsLastError = nats.LastError,
                redisConnected,
                redisPingMs
            },
            Database: new
            {
                connected = dbConnected,
                provider = db.Database.ProviderName
            },
            ProcessMetrics: new
            {
                workingSetBytes,
                workingSetFormatted = FormatBytes(workingSetBytes),
                gcHeapBytes,
                gcHeapFormatted = FormatBytes(gcHeapBytes),
                threadCount = proc.Threads.Count,
                uptimeSeconds = (long)uptime.TotalSeconds,
                uptimeFormatted = FormatUptime(uptime),
                startTimeUtc = startTime
            },
            ThreadPool: new
            {
                workerAvailable = availableWorker,
                workerMin = minWorker,
                workerMax = maxWorker,
                ioAvailable = availableIo,
                ioMin = minIo,
                ioMax = maxIo,
                workerBusy = maxWorker - availableWorker,
                ioBusy = maxIo - availableIo
            },
            Business: new
            {
                available = true
            }
        );

        return Result<RealtimeStatsDto>.Success(stats);
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        return $"{uptime.Minutes}m {uptime.Seconds}s";
    }
}
