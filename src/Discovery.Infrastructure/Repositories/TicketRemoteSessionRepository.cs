using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketRemoteSessionRepository : ITicketRemoteSessionRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketRemoteSessionRepository(DiscoveryDbContext db) => _db = db;

    public async Task<IEnumerable<TicketRemoteSession>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default)
        => await _db.TicketRemoteSessions
            .AsNoTracking()
            .Where(s => s.TicketId == ticketId)
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(ct);

    public async Task<TicketRemoteSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.TicketRemoteSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<TicketRemoteSession> CreateAsync(TicketRemoteSession session, CancellationToken ct = default)
    {
        _db.TicketRemoteSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<TicketRemoteSession> UpdateAsync(TicketRemoteSession session, CancellationToken ct = default)
    {
        _db.TicketRemoteSessions.Update(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }
}
