using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketKnowledgeLinkRepository : ITicketKnowledgeLinkRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketKnowledgeLinkRepository(DiscoveryDbContext db) => _db = db;

    public async Task<List<TicketKnowledgeLink>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default)
    {
        return await _db.TicketKnowledgeLinks
            .AsNoTracking()
            .Where(l => l.TicketId == ticketId)
            .OrderByDescending(l => l.LinkedAt)
            .ToListAsync(ct);
    }

    public async Task<TicketKnowledgeLink?> GetByTicketAndArticleAsync(Guid ticketId, Guid articleId, CancellationToken ct = default)
    {
        return await _db.TicketKnowledgeLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TicketId == ticketId && l.ArticleId == articleId, ct);
    }

    public async Task<TicketKnowledgeLink> CreateAsync(TicketKnowledgeLink link, CancellationToken ct = default)
    {
        link.Id = IdGenerator.NewId();
        link.LinkedAt = DateTime.UtcNow;
        _db.TicketKnowledgeLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        return link;
    }

    public async Task DeleteAsync(Guid linkId, CancellationToken ct = default)
    {
        var link = await _db.TicketKnowledgeLinks.FindAsync([linkId], ct);
        if (link is not null)
        {
            _db.TicketKnowledgeLinks.Remove(link);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task SetFeedbackAsync(Guid linkId, bool useful, CancellationToken ct = default)
    {
        await _db.TicketKnowledgeLinks
            .Where(l => l.Id == linkId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(l => l.FeedbackUseful, useful)
                .SetProperty(l => l.FeedbackAt, DateTime.UtcNow), ct);
    }
}
