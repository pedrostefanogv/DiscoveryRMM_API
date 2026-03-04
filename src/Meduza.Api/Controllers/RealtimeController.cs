using Meduza.Api.Hubs;
using Meduza.Api.Services;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using NATS.Client.Core;
using StackExchange.Redis;
using System.Diagnostics;
using System.Net.Sockets;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/realtime")]
public class RealtimeController : ControllerBase
{
    private readonly IAgentMessaging _messaging;
    private readonly IRedisService _redisService;
    private readonly NatsConnection _natsConnection;
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly IDbConnectionFactory _dbConnectionFactory;
    private readonly IConfiguration _configuration;

    public RealtimeController(
        IAgentMessaging messaging,
        IRedisService redisService,
        NatsConnection natsConnection,
        IConnectionMultiplexer redisConnection,
        IDbConnectionFactory dbConnectionFactory,
        IConfiguration configuration)
    {
        _messaging = messaging;
        _redisService = redisService;
        _natsConnection = natsConnection;
        _redisConnection = redisConnection;
        _dbConnectionFactory = dbConnectionFactory;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            natsConnected = _messaging.IsConnected,
            signalrConnectedAgents = AgentHub.ConnectedAgentCount,
            redisConnected = _redisService.IsConnected,
            checkedAtUtc = DateTime.UtcNow
        });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var process = Process.GetCurrentProcess();
        var startedAtUtc = process.StartTime.ToUniversalTime();
        var uptime = now - startedAtUtc;

        var redisPingMs = await TryGetRedisPingMsAsync();
        var databaseConnected = TryOpenDatabaseConnection();
        var businessStats = await TryGetBusinessStatsAsync(cancellationToken);
        var natsUrl = _configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
        var natsTcpReachable = await TryCheckTcpAsync(natsUrl);

        return Ok(new
        {
            checkedAtUtc = now,
            application = new
            {
                environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                machineName = Environment.MachineName,
                processId = Environment.ProcessId,
                startedAtUtc,
                uptimeSeconds = (long)uptime.TotalSeconds
            },
            realtime = new
            {
                signalrConnectedAgents = AgentHub.ConnectedAgentCount,
                natsConnected = _messaging.IsConnected,
                natsConnectionState = _natsConnection.ConnectionState.ToString(),
                natsTcpReachable,
                redisConnected = _redisService.IsConnected,
                redisConnectionState = _redisConnection.IsConnected ? "Connected" : "Disconnected",
                redisPingMs
            },
            database = new
            {
                provider = _configuration.GetValue<string>("Database:Provider") ?? "Postgres",
                connected = databaseConnected
            },
            business = businessStats,
            processMetrics = new
            {
                workingSetMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 2),
                gcManagedMemoryMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 2),
                threadCount = process.Threads.Count
            },
            threadPool = new
            {
                availableWorkers = GetAvailableWorkers(),
                availableIo = GetAvailableIo(),
                minWorkers = GetMinWorkers(),
                minIo = GetMinIo()
            }
        });
    }

    private bool TryOpenDatabaseConnection()
    {
        try
        {
            using var connection = _dbConnectionFactory.CreateConnection();
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<long?> TryGetRedisPingMsAsync()
    {
        try
        {
            var db = _redisConnection.GetDatabase();
            var ping = await db.PingAsync();
            return (long)ping.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TryCheckTcpAsync(string natsUrl)
    {
        try
        {
            var uri = new Uri(natsUrl);
            var host = uri.Host;
            var port = uri.Port;

            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(2000);
            var completed = await Task.WhenAny(connectTask, timeoutTask);
            return completed == connectTask && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<object> TryGetBusinessStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var onlineGraceSeconds = _configuration.GetValue<int?>("Realtime:AgentOnlineGraceSeconds") ?? 120;
            var onlineCutoffUtc = DateTime.UtcNow.AddSeconds(-onlineGraceSeconds);

            using var connection = _dbConnectionFactory.CreateConnection();
            var stats = await connection.QuerySingleAsync<BusinessStatsRow>(
                new CommandDefinition(
                    """
                    SELECT
                        (SELECT COUNT(*) FROM clients) AS TotalClients,
                        (SELECT COUNT(*) FROM sites) AS TotalSites,
                        (SELECT COUNT(*) FROM agents) AS TotalAgents,
                        (SELECT COUNT(*) FROM agents WHERE status = 0 AND last_seen_at >= @OnlineCutoffUtc) AS AgentsOnline,
                        (SELECT COUNT(*) FROM agents WHERE status = 1 OR (status = 0 AND (last_seen_at IS NULL OR last_seen_at < @OnlineCutoffUtc))) AS AgentsOffline,
                        (SELECT COUNT(*) FROM agents WHERE status = 0 AND (last_seen_at IS NULL OR last_seen_at < @OnlineCutoffUtc)) AS AgentsStale,
                        (SELECT COUNT(*) FROM agents WHERE status = 2) AS AgentsMaintenance,
                        (SELECT COUNT(*) FROM agents WHERE status = 3) AS AgentsError,
                        (SELECT COUNT(*) FROM agent_commands) AS TotalCommands,
                        (SELECT COUNT(*) FROM agent_commands WHERE status = 0) AS CommandsPending,
                        (SELECT COUNT(*) FROM agent_commands WHERE status = 1) AS CommandsSent,
                        (SELECT COUNT(*) FROM agent_commands WHERE status = 2) AS CommandsRunning,
                        (SELECT COUNT(*) FROM agent_commands WHERE status = 3) AS CommandsCompleted,
                        (SELECT COUNT(*) FROM agent_commands WHERE status IN (4,6)) AS CommandsFailed,
                        (SELECT COUNT(*) FROM tickets) AS TotalTickets,
                        (SELECT COUNT(*) FROM tickets WHERE closed_at IS NULL) AS TicketsOpen,
                        (SELECT COUNT(*) FROM tickets WHERE closed_at IS NOT NULL) AS TicketsClosed
                    """,
                    new { OnlineCutoffUtc = onlineCutoffUtc },
                    cancellationToken: cancellationToken));

            return new
            {
                available = true,
                clients = new
                {
                    total = stats.TotalClients
                },
                sites = new
                {
                    total = stats.TotalSites
                },
                agents = new
                {
                    total = stats.TotalAgents,
                    online = stats.AgentsOnline,
                    offline = stats.AgentsOffline,
                    stale = stats.AgentsStale,
                    maintenance = stats.AgentsMaintenance,
                    error = stats.AgentsError,
                    onlineGraceSeconds
                },
                commands = new
                {
                    total = stats.TotalCommands,
                    pending = stats.CommandsPending,
                    sent = stats.CommandsSent,
                    running = stats.CommandsRunning,
                    completed = stats.CommandsCompleted,
                    failed = stats.CommandsFailed
                },
                tickets = new
                {
                    total = stats.TotalTickets,
                    open = stats.TicketsOpen,
                    closed = stats.TicketsClosed
                }
            };
        }
        catch
        {
            return new
            {
                available = false
            };
        }
    }

    private static int GetAvailableWorkers()
    {
        ThreadPool.GetAvailableThreads(out var workerThreads, out _);
        return workerThreads;
    }

    private static int GetAvailableIo()
    {
        ThreadPool.GetAvailableThreads(out _, out var ioThreads);
        return ioThreads;
    }

    private static int GetMinWorkers()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out _);
        return workerThreads;
    }

    private static int GetMinIo()
    {
        ThreadPool.GetMinThreads(out _, out var ioThreads);
        return ioThreads;
    }

    private sealed record BusinessStatsRow(
        long TotalClients,
        long TotalSites,
        long TotalAgents,
        long AgentsOnline,
        long AgentsOffline,
        long AgentsStale,
        long AgentsMaintenance,
        long AgentsError,
        long TotalCommands,
        long CommandsPending,
        long CommandsSent,
        long CommandsRunning,
        long CommandsCompleted,
        long CommandsFailed,
        long TotalTickets,
        long TicketsOpen,
        long TicketsClosed);
}
