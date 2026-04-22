using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketAutomationLinkRepository : ITicketAutomationLinkRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketAutomationLinkRepository(DiscoveryDbContext db) => _db = db;

    public async Task<IReadOnlyList<TicketAutomationLink>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default)
        => await _db.TicketAutomationLinks
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId)
            .OrderByDescending(l => l.RequestedAt)
            .ToListAsync(ct);

    public async Task<TicketAutomationLink?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.TicketAutomationLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<TicketAutomationLink> CreateAsync(TicketAutomationLink link, CancellationToken ct = default)
    {
        link.Id = Guid.NewGuid();
        link.RequestedAt = DateTime.UtcNow;
        _db.TicketAutomationLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        return link;
    }

    public async Task UpdateAsync(TicketAutomationLink link, CancellationToken ct = default)
    {
        _db.TicketAutomationLinks.Update(link);
        await _db.SaveChangesAsync(ct);
    }
}
