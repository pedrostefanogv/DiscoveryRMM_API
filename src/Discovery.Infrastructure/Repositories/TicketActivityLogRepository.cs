using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketActivityLogRepository : ITicketActivityLogRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketActivityLogRepository(DiscoveryDbContext db) => _db = db;

    public async Task<TicketActivityLog> LogAsync(TicketActivityLog log)
    {
        log.Id = Guid.NewGuid();
        log.CreatedAt = DateTime.UtcNow;

        _db.Set<TicketActivityLog>().Add(log);
        await _db.SaveChangesAsync();

        return log;
    }

    public async Task<List<TicketActivityLog>> GetByTicketAsync(Guid ticketId)
    {
        return await _db.Set<TicketActivityLog>()
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<TicketActivityLog>> GetByTypeAsync(Guid ticketId, TicketActivityType type)
    {
        return await _db.Set<TicketActivityLog>()
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId && l.Type == type)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<TicketActivityLog>> GetByUserAsync(Guid ticketId, Guid userId)
    {
        return await _db.Set<TicketActivityLog>()
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId && l.ChangedByUserId == userId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<TicketActivityLog>> GetByDateRangeAsync(Guid ticketId, DateTime from, DateTime to)
    {
        return await _db.Set<TicketActivityLog>()
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId && l.CreatedAt >= from && l.CreatedAt <= to)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<TicketActivityLog?> GetLastActivityAsync(Guid ticketId)
    {
        return await _db.Set<TicketActivityLog>()
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetActivityCountAsync(Guid ticketId)
    {
        return await _db.Set<TicketActivityLog>()
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId)
            .CountAsync();
    }
}
