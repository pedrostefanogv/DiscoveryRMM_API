using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class LogRepository : ILogRepository
{
    private readonly MeduzaDbContext _db;

    public LogRepository(MeduzaDbContext db) => _db = db;

    public async Task<LogEntry> CreateAsync(LogEntry entry)
    {
        entry.Id = IdGenerator.NewId();
        entry.CreatedAt = DateTime.UtcNow;

        _db.Logs.Add(entry);
        await _db.SaveChangesAsync();

        return entry;
    }

    public async Task<IEnumerable<LogEntry>> QueryAsync(LogQuery query)
    {
        IQueryable<LogEntry> logQuery = _db.Logs.AsNoTracking();

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
        if (query.From.HasValue)
            logQuery = logQuery.Where(log => log.CreatedAt >= query.From.Value);
        if (query.To.HasValue)
            logQuery = logQuery.Where(log => log.CreatedAt <= query.To.Value);

        var limit = Math.Clamp(query.Limit, 1, 500);
        var offset = query.Offset < 0 ? 0 : query.Offset;

        return await logQuery
            .OrderByDescending(log => log.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> PurgeAsync(DateTime cutoff)
    {
        return await _db.Logs
            .Where(log => log.CreatedAt < cutoff)
            .ExecuteDeleteAsync();
    }
}
