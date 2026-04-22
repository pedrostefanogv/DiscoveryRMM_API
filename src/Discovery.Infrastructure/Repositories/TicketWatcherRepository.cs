using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketWatcherRepository : ITicketWatcherRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketWatcherRepository(DiscoveryDbContext db) => _db = db;

    public async Task<IEnumerable<TicketWatcher>> GetByTicketAsync(Guid ticketId)
        => await _db.TicketWatchers
            .AsNoTracking()
            .Where(w => w.TicketId == ticketId)
            .OrderBy(w => w.AddedAt)
            .ToListAsync();

    public async Task<TicketWatcher?> GetAsync(Guid ticketId, Guid userId)
        => await _db.TicketWatchers
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.TicketId == ticketId && w.UserId == userId);

    public async Task<TicketWatcher> AddAsync(Guid ticketId, Guid userId, string? addedBy)
    {
        var watcher = new TicketWatcher
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            UserId = userId,
            AddedBy = addedBy,
            AddedAt = DateTime.UtcNow
        };
        _db.TicketWatchers.Add(watcher);
        await _db.SaveChangesAsync();
        return watcher;
    }

    public async Task RemoveAsync(Guid ticketId, Guid userId)
        => await _db.TicketWatchers
            .Where(w => w.TicketId == ticketId && w.UserId == userId)
            .ExecuteDeleteAsync();
}
