using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int AbsoluteCacheTtlSeconds = 45;
    private const int DefaultOnlineGraceSeconds = 120;

    private readonly MeduzaDbContext _db;
    private readonly IRedisService _redisService;
    private readonly IConfigurationResolver _configurationResolver;

    public DashboardService(
        MeduzaDbContext db,
        IRedisService redisService,
        IConfigurationResolver configurationResolver)
    {
        _db = db;
        _redisService = redisService;
        _configurationResolver = configurationResolver;
    }

    public Task<DashboardSummaryDto> GetGlobalSummaryAsync(TimeSpan window, CancellationToken cancellationToken = default)
        => GetOrCreateSummaryAsync(
            DashboardCacheKeys.GlobalSummary(ToWindowHours(window)),
            () => BuildSummaryAsync(DashboardScopeLevel.Global, null, null, window, cancellationToken),
            cancellationToken);

    public Task<DashboardSummaryDto> GetClientSummaryAsync(Guid clientId, TimeSpan window, CancellationToken cancellationToken = default)
        => GetOrCreateSummaryAsync(
            DashboardCacheKeys.ClientSummary(clientId, ToWindowHours(window)),
            () => BuildSummaryAsync(DashboardScopeLevel.Client, clientId, null, window, cancellationToken),
            cancellationToken);

    public Task<DashboardSummaryDto> GetSiteSummaryAsync(Guid clientId, Guid siteId, TimeSpan window, CancellationToken cancellationToken = default)
        => GetOrCreateSummaryAsync(
            DashboardCacheKeys.SiteSummary(clientId, siteId, ToWindowHours(window)),
            () => BuildSummaryAsync(DashboardScopeLevel.Site, clientId, siteId, window, cancellationToken),
            cancellationToken);

    private async Task<DashboardSummaryDto> BuildSummaryAsync(
        DashboardScopeLevel level,
        Guid? clientId,
        Guid? siteId,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Subtract(window);

        var scopedAgents = BuildScopedAgentsQuery(level, clientId, siteId);
        var scopedTickets = BuildScopedTicketsQuery(level, clientId, siteId);
        var scopedLogsWindow = BuildScopedLogsWindowQuery(level, clientId, siteId, windowStartUtc);

        var agentSummaries = await scopedAgents
            .Select(a => new AgentStatusProjection(a.SiteId, a.Status, a.LastSeenAt))
            .ToListAsync(cancellationToken);

        var graceBySite = await ResolveGraceBySiteAsync(agentSummaries.Select(agent => agent.SiteId));

        var agentsTotal = agentSummaries.Count;
        var agentsOnline = 0;
        var agentsStale = 0;
        var agentsMaintenance = 0;
        var agentsError = 0;
        var agentsOfflinePersisted = 0;

        foreach (var agent in agentSummaries)
        {
            switch (agent.Status)
            {
                case AgentStatus.Online:
                {
                    var graceSeconds = graceBySite.GetValueOrDefault(agent.SiteId, DefaultOnlineGraceSeconds);
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
        var onlineGraceSeconds = await ResolveReportedGraceSecondsAsync(level, clientId, siteId, graceBySite);

        var scopedCommands = BuildScopedCommandsQuery(level, clientId, siteId).Where(c => c.CreatedAt >= windowStartUtc);
        var commandCounts = await scopedCommands
            .GroupBy(c => c.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);

        var commandsPending = GetCommandCount(commandCounts, CommandStatus.Pending);
        var commandsSent = GetCommandCount(commandCounts, CommandStatus.Sent);
        var commandsRunning = GetCommandCount(commandCounts, CommandStatus.Running);
        var commandsCompleted = GetCommandCount(commandCounts, CommandStatus.Completed);
        var commandsFailed = GetCommandCount(commandCounts, CommandStatus.Failed)
            + GetCommandCount(commandCounts, CommandStatus.Timeout)
            + GetCommandCount(commandCounts, CommandStatus.Cancelled);
        var commandsTotal = commandCounts.Values.Sum();
        var commandsSuccessRate = CalculateSuccessRate(commandsCompleted, commandsFailed);

        var ticketsTotal = await scopedTickets.CountAsync(cancellationToken);
        var ticketsOpen = await scopedTickets.CountAsync(t => t.ClosedAt == null, cancellationToken);
        var ticketsClosed = await scopedTickets.CountAsync(t => t.ClosedAt != null, cancellationToken);
        var ticketsSlaBreachedOpen = await scopedTickets.CountAsync(
            t => t.ClosedAt == null && t.SlaBreached,
            cancellationToken);

        var logCounts = await scopedLogsWindow
            .GroupBy(l => l.Level)
            .Select(group => new { Level = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Level, item => item.Count, cancellationToken);

        var logsError = GetLogCount(logCounts, LogLevel.Error) + GetLogCount(logCounts, LogLevel.Fatal);
        var logsWarn = GetLogCount(logCounts, LogLevel.Warn);
        var logsInfo = GetLogCount(logCounts, LogLevel.Info) + GetLogCount(logCounts, LogLevel.Debug) + GetLogCount(logCounts, LogLevel.Trace);
        var logsTotal = logCounts.Values.Sum();

        var scopedAutomation = BuildScopedAutomationQuery(level, clientId, siteId)
            .Where(execution => execution.CreatedAt >= windowStartUtc);
        var automationCounts = await scopedAutomation
            .GroupBy(execution => execution.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);

        var automationDispatched = GetAutomationCount(automationCounts, AutomationExecutionStatus.Dispatched);
        var automationAcknowledged = GetAutomationCount(automationCounts, AutomationExecutionStatus.Acknowledged);
        var automationCompleted = GetAutomationCount(automationCounts, AutomationExecutionStatus.Completed);
        var automationFailed = GetAutomationCount(automationCounts, AutomationExecutionStatus.Failed);
        var automationTotal = automationCounts.Values.Sum();
        var automationSuccessRate = CalculateSuccessRate(automationCompleted, automationFailed);

        return new DashboardSummaryDto(
            Scope: new DashboardScopeDto(level.ToString().ToLowerInvariant(), clientId, siteId),
            Period: new DashboardPeriodDto(windowStartUtc, now, (int)Math.Round(window.TotalHours)),
            Agents: new DashboardAgentsSummaryDto(
                Total: agentsTotal,
                Online: agentsOnline,
                Offline: agentsOffline,
                Stale: agentsStale,
                Maintenance: agentsMaintenance,
                Error: agentsError,
                OnlineGraceSeconds: onlineGraceSeconds),
            Commands: new DashboardCommandsSummaryDto(
                Total: commandsTotal,
                Pending: commandsPending,
                Sent: commandsSent,
                Running: commandsRunning,
                Completed: commandsCompleted,
                Failed: commandsFailed,
                SuccessRate: commandsSuccessRate),
            Tickets: new DashboardTicketsSummaryDto(
                Total: ticketsTotal,
                Open: ticketsOpen,
                Closed: ticketsClosed,
                SlaBreachedOpen: ticketsSlaBreachedOpen),
            Logs: new DashboardLogsSummaryDto(
                Total: logsTotal,
                Error: logsError,
                Warn: logsWarn,
                Info: logsInfo),
            Automation: new DashboardAutomationSummaryDto(
                Total: automationTotal,
                Dispatched: automationDispatched,
                Acknowledged: automationAcknowledged,
                Completed: automationCompleted,
                Failed: automationFailed,
                SuccessRate: automationSuccessRate),
            GeneratedAtUtc: now);
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
                return (siteId, grace: DefaultOnlineGraceSeconds);
            }
        });

        var values = await Task.WhenAll(tasks);
        return values.ToDictionary(item => item.siteId, item => item.grace);
    }

    private async Task<int> ResolveReportedGraceSecondsAsync(
        DashboardScopeLevel level,
        Guid? clientId,
        Guid? siteId,
        IReadOnlyDictionary<Guid, int> graceBySite)
    {
        if (level == DashboardScopeLevel.Site && siteId.HasValue)
            return graceBySite.GetValueOrDefault(siteId.Value, DefaultOnlineGraceSeconds);

        if (graceBySite.Count > 0)
        {
            var distinct = graceBySite.Values.Distinct().ToArray();
            if (distinct.Length == 1)
                return distinct[0];
        }

        if (level == DashboardScopeLevel.Client && clientId.HasValue)
        {
            var effective = await _configurationResolver.GetEffectiveValueAsync<int>("Client", "AgentOnlineGraceSeconds", clientId.Value);
            if (effective > 0)
                return effective;
        }

        var server = await _configurationResolver.GetServerAsync();
        return server.AgentOnlineGraceSeconds;
    }

    private async Task<DashboardSummaryDto> GetOrCreateSummaryAsync(
        string cacheKey,
        Func<Task<DashboardSummaryDto>> factory,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var cached = await TryGetCachedSummaryAsync(cacheKey);
        if (cached is not null)
            return cached;

        var summary = await factory();
        var payload = JsonSerializer.Serialize(summary, JsonOptions);
        await _redisService.SetAsync(cacheKey, payload, AbsoluteCacheTtlSeconds);
        return summary;
    }

    private async Task<DashboardSummaryDto?> TryGetCachedSummaryAsync(string cacheKey)
    {
        var cached = await _redisService.GetAsync(cacheKey);
        if (string.IsNullOrWhiteSpace(cached))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DashboardSummaryDto>(cached, JsonOptions);
        }
        catch (JsonException)
        {
            await _redisService.DeleteAsync(cacheKey);
            return null;
        }
    }

    private IQueryable<Agent> BuildScopedAgentsQuery(DashboardScopeLevel level, Guid? clientId, Guid? siteId)
    {
        var agents = _db.Agents.AsNoTracking();

        return level switch
        {
            DashboardScopeLevel.Global => agents,
            DashboardScopeLevel.Client =>
                from agent in agents
                join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
                where site.ClientId == clientId
                select agent,
            DashboardScopeLevel.Site => agents.Where(agent => agent.SiteId == siteId),
            _ => agents
        };
    }

    private IQueryable<Ticket> BuildScopedTicketsQuery(DashboardScopeLevel level, Guid? clientId, Guid? siteId)
    {
        var tickets = _db.Tickets
            .AsNoTracking()
            .Where(t => t.DeletedAt == null);

        return level switch
        {
            DashboardScopeLevel.Global => tickets,
            DashboardScopeLevel.Client => tickets.Where(t => t.ClientId == clientId),
            DashboardScopeLevel.Site => tickets.Where(t => t.SiteId == siteId),
            _ => tickets
        };
    }

    private IQueryable<LogEntry> BuildScopedLogsWindowQuery(
        DashboardScopeLevel level,
        Guid? clientId,
        Guid? siteId,
        DateTime windowStartUtc)
    {
        var logs = _db.Logs
            .AsNoTracking()
            .Where(log => log.CreatedAt >= windowStartUtc);

        if (level == DashboardScopeLevel.Global)
            return logs;

        if (level == DashboardScopeLevel.Client)
        {
            var clientSiteIds = _db.Sites.AsNoTracking()
                .Where(site => site.ClientId == clientId)
                .Select(site => site.Id);

            var clientAgentIds = _db.Agents.AsNoTracking()
                .Where(agent => clientSiteIds.Contains(agent.SiteId))
                .Select(agent => agent.Id);

            return logs.Where(log =>
                log.ClientId == clientId
                || (log.SiteId.HasValue && clientSiteIds.Contains(log.SiteId.Value))
                || (log.AgentId.HasValue && clientAgentIds.Contains(log.AgentId.Value)));
        }

        var siteAgentIds = _db.Agents.AsNoTracking()
            .Where(agent => agent.SiteId == siteId)
            .Select(agent => agent.Id);

        return logs.Where(log =>
            log.SiteId == siteId
            || (log.AgentId.HasValue && siteAgentIds.Contains(log.AgentId.Value)));
    }

    private IQueryable<AgentCommand> BuildScopedCommandsQuery(DashboardScopeLevel level, Guid? clientId, Guid? siteId)
    {
        var commands = _db.AgentCommands.AsNoTracking();
        if (level == DashboardScopeLevel.Global)
            return commands;

        var scopedAgents = BuildScopedAgentsQuery(level, clientId, siteId).Select(agent => agent.Id);
        return commands.Where(command => scopedAgents.Contains(command.AgentId));
    }

    private IQueryable<AutomationExecutionReport> BuildScopedAutomationQuery(
        DashboardScopeLevel level,
        Guid? clientId,
        Guid? siteId)
    {
        var executions = _db.AutomationExecutionReports.AsNoTracking();
        if (level == DashboardScopeLevel.Global)
            return executions;

        var scopedAgents = BuildScopedAgentsQuery(level, clientId, siteId).Select(agent => agent.Id);
        return executions.Where(execution => scopedAgents.Contains(execution.AgentId));
    }

    private static int GetCommandCount(IReadOnlyDictionary<CommandStatus, int> counts, CommandStatus status)
        => counts.TryGetValue(status, out var value) ? value : 0;

    private static int GetLogCount(IReadOnlyDictionary<LogLevel, int> counts, LogLevel level)
        => counts.TryGetValue(level, out var value) ? value : 0;

    private static int GetAutomationCount(IReadOnlyDictionary<AutomationExecutionStatus, int> counts, AutomationExecutionStatus status)
        => counts.TryGetValue(status, out var value) ? value : 0;

    private static double CalculateSuccessRate(int completed, int failed)
    {
        var total = completed + failed;
        if (total <= 0)
            return 0;

        return Math.Round(completed * 100d / total, 2);
    }

    private static int ToWindowHours(TimeSpan window)
        => (int)Math.Clamp(Math.Round(window.TotalHours), 1, 168);

    private readonly record struct AgentStatusProjection(Guid SiteId, AgentStatus Status, DateTime? LastSeenAt);

    private enum DashboardScopeLevel
    {
        Global = 0,
        Client = 1,
        Site = 2
    }
}
