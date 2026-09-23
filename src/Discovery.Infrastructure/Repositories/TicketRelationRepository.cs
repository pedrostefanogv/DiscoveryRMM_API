using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketRelationRepository(DiscoveryDbContext db) : ITicketRelationRepository
{
    public async Task<IReadOnlyList<TicketRelation>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default)
        => await db.TicketRelations
            .AsNoTracking()
            .Where(r => r.SourceTicketId == ticketId || r.TargetTicketId == ticketId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    public async Task<TicketRelation?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.TicketRelations.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<bool> ExistsAsync(Guid sourceTicketId, Guid targetTicketId, TicketRelationType type, CancellationToken ct = default)
    {
        var value = (int)type;
        return await db.TicketRelations.AnyAsync(
            r => r.SourceTicketId == sourceTicketId && r.TargetTicketId == targetTicketId && r.RelationTypeValue == value, ct);
    }

    public async Task<bool> ExistsReverseAsync(Guid sourceTicketId, Guid targetTicketId, CancellationToken ct = default)
        => await db.TicketRelations.AnyAsync(
            r => r.SourceTicketId == targetTicketId && r.TargetTicketId == sourceTicketId, ct);

    public async Task<TicketRelation> AddAsync(TicketRelation relation, CancellationToken ct = default)
    {
        db.TicketRelations.Add(relation);
        await db.SaveChangesAsync(ct);
        return relation;
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var relation = await db.TicketRelations.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (relation is null)
            return;
        db.TicketRelations.Remove(relation);
        await db.SaveChangesAsync(ct);
    }
}
