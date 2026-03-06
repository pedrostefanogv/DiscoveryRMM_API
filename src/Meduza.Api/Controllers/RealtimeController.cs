using Meduza.Api.Hubs;
using Meduza.Api.Services;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using StackExchange.Redis;
using System.Diagnostics;
using System.Net.Sockets;
using AgentCommandStatus = Meduza.Core.Enums.CommandStatus;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/realtime")]
public class RealtimeController : ControllerBase
{
    private readonly IAgentMessaging _messaging;
    private readonly IRedisService _redisService;
    private readonly NatsConnection _natsConnection;
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly MeduzaDbContext _dbContext;
    private readonly IConfiguration _configuration;

    public RealtimeController(
        IAgentMessaging messaging,
        IRedisService redisService,
        NatsConnection natsConnection,
        IConnectionMultiplexer redisConnection,
        MeduzaDbContext dbContext,
        IConfiguration configuration)
    {
        _messaging = messaging;
        _redisService = redisService;
        _natsConnection = natsConnection;
        _redisConnection = redisConnection;
        _dbContext = dbContext;
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
        var databaseConnected = await TryOpenDatabaseConnectionAsync(cancellationToken);
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

    private async Task<bool> TryOpenDatabaseConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _dbContext.Database.CanConnectAsync(cancellationToken);
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

            var totalClients = await _dbContext.Clients.AsNoTracking().CountAsync(cancellationToken);
            var totalSites = await _dbContext.Sites.AsNoTracking().CountAsync(cancellationToken);
            var totalAgents = await _dbContext.Agents.AsNoTracking().CountAsync(cancellationToken);
            var agentsOnline = await _dbContext.Agents.AsNoTracking()
                .CountAsync(agent => agent.Status == AgentStatus.Online && agent.LastSeenAt >= onlineCutoffUtc, cancellationToken);
            var agentsOffline = await _dbContext.Agents.AsNoTracking()
                .CountAsync(agent => agent.Status == AgentStatus.Offline ||
                    (agent.Status == AgentStatus.Online && (agent.LastSeenAt == null || agent.LastSeenAt < onlineCutoffUtc)), cancellationToken);
            var agentsStale = await _dbContext.Agents.AsNoTracking()
                .CountAsync(agent => agent.Status == AgentStatus.Online && (agent.LastSeenAt == null || agent.LastSeenAt < onlineCutoffUtc), cancellationToken);
            var agentsMaintenance = await _dbContext.Agents.AsNoTracking()
                .CountAsync(agent => agent.Status == AgentStatus.Maintenance, cancellationToken);
            var agentsError = await _dbContext.Agents.AsNoTracking()
                .CountAsync(agent => agent.Status == AgentStatus.Error, cancellationToken);

            var totalCommands = await _dbContext.AgentCommands.AsNoTracking().CountAsync(cancellationToken);
            var commandsPending = await _dbContext.AgentCommands.AsNoTracking()
                .CountAsync(command => command.Status == AgentCommandStatus.Pending, cancellationToken);
            var commandsSent = await _dbContext.AgentCommands.AsNoTracking()
                .CountAsync(command => command.Status == AgentCommandStatus.Sent, cancellationToken);
            var commandsRunning = await _dbContext.AgentCommands.AsNoTracking()
                .CountAsync(command => command.Status == AgentCommandStatus.Running, cancellationToken);
            var commandsCompleted = await _dbContext.AgentCommands.AsNoTracking()
                .CountAsync(command => command.Status == AgentCommandStatus.Completed, cancellationToken);
            var commandsFailed = await _dbContext.AgentCommands.AsNoTracking()
                .CountAsync(command => command.Status == AgentCommandStatus.Failed || command.Status == AgentCommandStatus.Timeout, cancellationToken);

            var totalTickets = await _dbContext.Tickets.AsNoTracking().CountAsync(cancellationToken);
            var ticketsOpen = await _dbContext.Tickets.AsNoTracking()
                .CountAsync(ticket => ticket.ClosedAt == null, cancellationToken);
            var ticketsClosed = await _dbContext.Tickets.AsNoTracking()
                .CountAsync(ticket => ticket.ClosedAt != null, cancellationToken);

            return new
            {
                available = true,
                clients = new
                {
                    total = totalClients
                },
                sites = new
                {
                    total = totalSites
                },
                agents = new
                {
                    total = totalAgents,
                    online = agentsOnline,
                    offline = agentsOffline,
                    stale = agentsStale,
                    maintenance = agentsMaintenance,
                    error = agentsError,
                    onlineGraceSeconds
                },
                commands = new
                {
                    total = totalCommands,
                    pending = commandsPending,
                    sent = commandsSent,
                    running = commandsRunning,
                    completed = commandsCompleted,
                    failed = commandsFailed
                },
                tickets = new
                {
                    total = totalTickets,
                    open = ticketsOpen,
                    closed = ticketsClosed
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
}
