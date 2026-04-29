using Discovery.Api.Hubs;
using Discovery.Api.Services;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using StackExchange.Redis;
using System.Diagnostics;
using System.Net.Sockets;
using AgentCommandStatus = Discovery.Core.Enums.CommandStatus;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/realtime")]
public class RealtimeController : ControllerBase
{
    private readonly IAgentMessaging _messaging;
    private readonly IRedisService _redisService;
    private readonly NatsConnection _natsConnection;
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly DiscoveryDbContext _dbContext;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IConfiguration _configuration;
    private readonly IConfigurationService _configurationService;

    public RealtimeController(
        IAgentMessaging messaging,
        IRedisService redisService,
        NatsConnection natsConnection,
        IConnectionMultiplexer redisConnection,
        DiscoveryDbContext dbContext,
        IConfigurationResolver configurationResolver,
        IConfiguration configuration,
        IConfigurationService configurationService)
    {
        _messaging = messaging;
        _redisService = redisService;
        _natsConnection = natsConnection;
        _redisConnection = redisConnection;
        _dbContext = dbContext;
        _configurationResolver = configurationResolver;
        _configuration = configuration;
        _configurationService = configurationService;
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
        var serverConfig = await _configurationService.GetServerConfigAsync();
        var natsHost = !string.IsNullOrWhiteSpace(serverConfig.NatsServerHostInternal)
            ? serverConfig.NatsServerHostInternal
            : GetHostFromUrlOrDefault(_configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222");
        var natsUrl = $"nats://{natsHost}:4222";
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

    private static string GetHostFromUrlOrDefault(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return "localhost";
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
            var serverConfig = await _configurationResolver.GetServerAsync();
            var onlineGraceSeconds = serverConfig.AgentOnlineGraceSeconds;
            var agents = await _dbContext.Agents.AsNoTracking()
                .Select(agent => new AgentStatusProjection(agent.SiteId, agent.Status, agent.LastSeenAt))
                .ToListAsync(cancellationToken);

            var graceBySite = await ResolveGraceBySiteAsync(agents.Select(agent => agent.SiteId));

            var totalClients = await _dbContext.Clients.AsNoTracking().CountAsync(cancellationToken);
            var totalSites = await _dbContext.Sites.AsNoTracking().CountAsync(cancellationToken);
            var totalAgents = agents.Count;
            var agentsOnline = 0;
            var agentsStale = 0;
            var agentsMaintenance = 0;
            var agentsError = 0;
            var agentsOfflinePersisted = 0;

            var now = DateTime.UtcNow;
            foreach (var agent in agents)
            {
                switch (agent.Status)
                {
                    case AgentStatus.Online:
                    {
                        var graceSeconds = graceBySite.GetValueOrDefault(agent.SiteId, 120);
                        var cutoffUtc = now.AddSeconds(-graceSeconds);
                        if (agent.LastSeenAt.HasValue && agent.LastSeenAt.Value >= cutoffUtc)
                            agentsOnline++;
                        else
                            agentsStale++;
                        break;
                    }
                    case AgentStatus.Offline:
                        agentsOfflinePersisted++;
                        break;
                    case AgentStatus.Maintenance:
                        agentsMaintenance++;
                        break;
                    case AgentStatus.Error:
                        agentsError++;
                        break;
                }
            }

            var agentsOffline = agentsOfflinePersisted + agentsStale;

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

    private async Task<Dictionary<Guid, int>> ResolveGraceBySiteAsync(IEnumerable<Guid> siteIds)
    {
        var distinctIds = siteIds.Distinct().ToList();
        if (distinctIds.Count == 0)
            return new Dictionary<Guid, int>();

        var tasks = distinctIds.Select(async siteId =>
        {
            try
            {
                var resolved = await _configurationResolver.ResolveForSiteAsync(siteId);
                return (siteId, grace: resolved.AgentOnlineGraceSeconds);
            }
            catch
            {
                return (siteId, grace: 120);
            }
        });

        var values = await Task.WhenAll(tasks);
        return values.ToDictionary(item => item.siteId, item => item.grace);
    }

    private readonly record struct AgentStatusProjection(Guid SiteId, AgentStatus Status, DateTime? LastSeenAt);

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
