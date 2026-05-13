using Discovery.Core.Entities;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class LogRepository : ILogRepository
{
    private readonly DiscoveryDbContext _db;
    private readonly IAgentMessaging _messaging;

    public LogRepository(DiscoveryDbContext db, IAgentMessaging messaging)
    {
        _db = db;
        _messaging = messaging;
    }

    public async Task<LogEntry> CreateAsync(LogEntry entry)
    {
        entry.Id = IdGenerator.NewId();
        entry.CreatedAt = DateTime.UtcNow;

        _db.Logs.Add(entry);
        await _db.SaveChangesAsync();
        await _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(
                "LogCreated",
                new
                {
                    logId = entry.Id,
                    entry.ClientId,
                    entry.SiteId,
                    entry.AgentId,
                    level = entry.Level.ToString(),
                    type = entry.Type.ToString()
                },
                entry.ClientId,
                entry.SiteId));

        return entry;
    }

    public async Task<IEnumerable<LogEntry>> QueryAsync(LogQuery query)
    {
        var logQuery = BuildFilteredQuery(query);

        var limit = Math.Clamp(query.Limit, 1, 500);
        var offset = query.Offset < 0 ? 0 : query.Offset;

        return await logQuery
            .OrderByDescending(log => log.CreatedAt)
            .ThenByDescending(log => log.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<LogEntry>> QueryPageAsync(LogQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);
        var logQuery = BuildFilteredQuery(query);

        if (query.CursorCreatedAtUtc.HasValue && query.CursorId.HasValue)
        {
            var cursorCreatedAtUtc = query.CursorCreatedAtUtc.Value;
            var cursorId = query.CursorId.Value;

            logQuery = logQuery.Where(log =>
                log.CreatedAt < cursorCreatedAtUtc ||
                (log.CreatedAt == cursorCreatedAtUtc && log.Id.CompareTo(cursorId) < 0));
        }

        return await logQuery
            .OrderByDescending(log => log.CreatedAt)
            .ThenByDescending(log => log.Id)
            .Take(limit + 1)
            .ToListAsync();
    }

    public async Task<int> PurgeAsync(DateTime cutoff)
    {
        return await _db.Logs
            .Where(log => log.CreatedAt < cutoff)
            .ExecuteDeleteAsync();
    }

    private IQueryable<LogEntry> BuildFilteredQuery(LogQuery query)
    {
        IQueryable<LogEntry> logQuery = _db.Logs.AsNoTracking();

        if (!query.HasGlobalAccess)
        {
            var allowedClientIds = query.AllowedClientIds.Distinct().ToArray();
            var allowedSiteIds = query.AllowedSiteIds.Distinct().ToArray();

            if (allowedClientIds.Length == 0 && allowedSiteIds.Length == 0)
                return Enumerable.Empty<LogEntry>().AsQueryable();

            logQuery = logQuery.Where(log =>
                (log.ClientId.HasValue && allowedClientIds.Contains(log.ClientId.Value)) ||
                (log.SiteId.HasValue && allowedSiteIds.Contains(log.SiteId.Value)));
        }

        if (query.ClientId.HasValue)
            logQuery = logQuery.Where(log => log.ClientId == query.ClientId);
        if (query.SiteId.HasValue)
            logQuery = logQuery.Where(log => log.SiteId == query.SiteId);
        if (query.AgentId.HasValue)
            logQuery = logQuery.Where(log => log.AgentId == query.AgentId);
        if (query.Type.HasValue)
            logQuery = logQuery.Where(log => log.Type == query.Type.Value);
        if (query.Level.HasValue)
            logQuery = logQuery.Where(log => log.Level == query.Level.Value);
        if (query.Source.HasValue)
            logQuery = logQuery.Where(log => log.Source == query.Source.Value);
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var search = query.SearchText.Trim().ToLowerInvariant();
            logQuery = logQuery.Where(log =>
                log.Message.ToLower().Contains(search) ||
                (log.DataJson != null && log.DataJson.ToLower().Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(query.TraceId))
        {
            var traceId = query.TraceId.Trim().ToLowerInvariant();
            logQuery = logQuery.Where(log =>
                log.DataJson != null &&
                log.DataJson.ToLower().Contains("\"traceid\"") &&
                log.DataJson.ToLower().Contains(traceId));
        }
        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            var correlationId = query.CorrelationId.Trim().ToLowerInvariant();
            logQuery = logQuery.Where(log =>
                log.DataJson != null &&
                log.DataJson.ToLower().Contains("\"correlationid\"") &&
                log.DataJson.ToLower().Contains(correlationId));
        }
        if (!string.IsNullOrWhiteSpace(query.RequestPath))
        {
            var requestPath = query.RequestPath.Trim().ToLowerInvariant();
            logQuery = logQuery.Where(log =>
                log.Message.ToLower().Contains(requestPath) ||
                (log.DataJson != null &&
                 ((log.DataJson.ToLower().Contains("\"path\"") || log.DataJson.ToLower().Contains("\"requestpath\"")) &&
                  log.DataJson.ToLower().Contains(requestPath))));
        }
        if (query.StatusCode.HasValue)
        {
            var statusCodeNeedle = $"\"statuscode\":{query.StatusCode.Value}";
            var statusCodeNeedleWithSpace = $"\"statuscode\": {query.StatusCode.Value}";
            logQuery = logQuery.Where(log =>
                log.DataJson != null &&
                (log.DataJson.ToLower().Contains(statusCodeNeedle) ||
                 log.DataJson.ToLower().Contains(statusCodeNeedleWithSpace)));
        }
        if (query.From.HasValue)
            logQuery = logQuery.Where(log => log.CreatedAt >= query.From.Value);
        if (query.To.HasValue)
            logQuery = logQuery.Where(log => log.CreatedAt <= query.To.Value);

        return logQuery;
    }
}
