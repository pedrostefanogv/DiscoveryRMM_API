using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class TicketActivityLogRepository : ITicketActivityLogRepository
{
    private readonly MeduzaDbContext _db;

    public TicketActivityLogRepository(MeduzaDbContext db) => _db = db;

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
