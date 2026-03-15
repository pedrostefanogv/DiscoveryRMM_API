using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Meduza.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private readonly MeduzaDbContext _db;
    private readonly IMemoryCache _memoryCache;
    private readonly int _agentOnlineGraceSeconds;

    public DashboardService(MeduzaDbContext db, IMemoryCache memoryCache, IConfiguration configuration)
    {
        _db = db;
        _memoryCache = memoryCache;
        _agentOnlineGraceSeconds = int.TryParse(configuration["Realtime:AgentOnlineGraceSeconds"], out var parsed)
            ? parsed
            : 120;
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
        var onlineCutoffUtc = now.AddSeconds(-_agentOnlineGraceSeconds);

        var scopedAgents = BuildScopedAgentsQuery(level, clientId, siteId);
        var scopedTickets = BuildScopedTicketsQuery(level, clientId, siteId);
        var scopedLogsWindow = BuildScopedLogsWindowQuery(level, clientId, siteId, windowStartUtc);

        var agentsTotal = await scopedAgents.CountAsync(cancellationToken);
        var agentsOnline = await scopedAgents
            .CountAsync(a => a.Status == AgentStatus.Online && a.LastSeenAt >= onlineCutoffUtc, cancellationToken);
        var agentsStale = await scopedAgents
            .CountAsync(a => a.Status == AgentStatus.Online && (a.LastSeenAt == null || a.LastSeenAt < onlineCutoffUtc), cancellationToken);
        var agentsMaintenance = await scopedAgents
            .CountAsync(a => a.Status == AgentStatus.Maintenance, cancellationToken);
        var agentsError = await scopedAgents
            .CountAsync(a => a.Status == AgentStatus.Error, cancellationToken);
        var agentsOfflinePersisted = await scopedAgents
            .CountAsync(a => a.Status == AgentStatus.Offline, cancellationToken);
        var agentsOffline = agentsOfflinePersisted + agentsStale;

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
                OnlineGraceSeconds: _agentOnlineGraceSeconds),
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

    private async Task<DashboardSummaryDto> GetOrCreateSummaryAsync(
        string cacheKey,
        Func<Task<DashboardSummaryDto>> factory,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(cacheKey, out DashboardSummaryDto? cached) && cached is not null)
            return cached;

        var summary = await factory();
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(45),
            SlidingExpiration = TimeSpan.FromSeconds(15)
        };

        _memoryCache.Set(cacheKey, summary, options);
        return summary;
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
        => (int)Math.Round(window.TotalHours);

    private enum DashboardScopeLevel
    {
        Global = 0,
        Client = 1,
        Site = 2
    }
}
