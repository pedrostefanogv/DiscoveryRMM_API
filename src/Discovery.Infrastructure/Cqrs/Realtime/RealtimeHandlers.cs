using System.Diagnostics;
using System.Runtime.InteropServices;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Realtime.Queries;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using NATS.Client.Core;

namespace Discovery.Infrastructure.Cqrs.Realtime;

public sealed class GetRealtimeStatusQueryHandler : IRequestHandler<GetRealtimeStatusQuery, Result<RealtimeStatusDto>>
{
    public Task<Result<RealtimeStatusDto>> Handle(GetRealtimeStatusQuery q, CancellationToken ct)
    {
        return Task.FromResult(Result<RealtimeStatusDto>.Success(new RealtimeStatusDto(0, 0, null)));
    }
}

public sealed class GetRealtimeStatsQueryHandler(
    NatsConnection natsConnection,
    IRedisService redis,
    DiscoveryDbContext db
) : IRequestHandler<GetRealtimeStatsQuery, Result<RealtimeStatsDto>>
{
    public async Task<Result<RealtimeStatsDto>> Handle(GetRealtimeStatsQuery q, CancellationToken ct)
    {
        var proc = Process.GetCurrentProcess();
        var startTime = proc.StartTime.ToUniversalTime();
        var uptime = DateTime.UtcNow - startTime;

        // ── Saúde da Plataforma ──────────────────────────────────────
        var natsConnected = natsConnection.ConnectionState == NatsConnectionState.Open;
        var natsUrl = natsConnection.Opts.Url ?? "";

        var redisConnected = redis.IsConnected;

        bool dbConnected;
        try { dbConnected = await db.Database.CanConnectAsync(ct).ConfigureAwait(false); }
        catch { dbConnected = false; }

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
                version = Environment.Version.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})",
                processId = Environment.ProcessId,
                machineName = Environment.MachineName
            },
            Realtime: new
            {
                natsConnected,
                natsUrl,
                redisConnected,
                dbConnected
            },
            Database: new
            {
                connected = dbConnected
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
