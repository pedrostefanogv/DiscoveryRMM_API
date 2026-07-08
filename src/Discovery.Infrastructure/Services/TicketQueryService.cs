using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação de ITicketQueryService usando EF Core com AsNoTracking.
/// Para queries de alta performance (Dashboard), usar Dapper diretamente.
/// </summary>
public sealed class TicketQueryService : ITicketQueryService
{
    private readonly DiscoveryDbContext _db;

    public TicketQueryService(DiscoveryDbContext db)
    {
        _db = db;
    }

    public async Task<CursorPageDto<TicketListItemDto>> ListTicketsAsync(
        TicketFilterQuery filter, CancellationToken ct = default)
    {
        var f = filter;
        var query = _db.Tickets.AsNoTracking().Where(t => t.DeletedAt == null);

        if (f.ClientId.HasValue) query = query.Where(t => t.ClientId == f.ClientId.Value);
        if (f.SiteId.HasValue) query = query.Where(t => t.SiteId == f.SiteId.Value);
        if (f.AgentId.HasValue) query = query.Where(t => t.AgentId == f.AgentId.Value);
        if (f.DepartmentId.HasValue) query = query.Where(t => t.DepartmentId == f.DepartmentId.Value);
        if (f.WorkflowStateId.HasValue) query = query.Where(t => t.WorkflowStateId == f.WorkflowStateId.Value);
        if (f.AssignedToUserId.HasValue) query = query.Where(t => t.AssignedToUserId == f.AssignedToUserId.Value);
        if (f.Priority.HasValue) query = query.Where(t => t.Priority == f.Priority.Value);
        if (f.SlaBreached.HasValue) query = query.Where(t => t.SlaBreached == f.SlaBreached.Value);
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var s = f.Text.ToLower();
            query = query.Where(t => t.Title.ToLower().Contains(s) || t.Description.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(f.Cursor) && Guid.TryParse(f.Cursor, out var cid))
            query = query.Where(t => t.CreatedAt < _db.Tickets.AsNoTracking().Where(x => x.Id == cid).Select(x => x.CreatedAt).FirstOrDefault());

        var limit = Math.Clamp(f.Limit, 1, 200);
        var items = await query.OrderByDescending(t => t.CreatedAt).Take(limit + 1)
            .Select(t => new TicketListItemDto(t.Id, t.ClientId, t.SiteId, t.Title, t.Priority,
                t.WorkflowStateId, t.AssignedToUserId, t.SlaBreached, t.CreatedAt, t.ClosedAt))
            .ToListAsync(ct);

        var hm = items.Count > limit;
        var pg = hm ? items.Take(limit).ToList() : items;
        var nc = hm && pg.Count > 0
            ? CursorPaginationHelper.EncodeCreatedAtCursor(pg[^1].CreatedAt, pg[^1].Id)
            : null;

        return new CursorPageDto<TicketListItemDto>(pg, pg.Count, f.Cursor, nc, hm, limit);
    }

    public async Task<TicketDetailDto?> GetTicketByIdAsync(Guid id, CancellationToken ct = default)
    {
        var t = await _db.Tickets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (t is null) return null;

        return new TicketDetailDto(t.Id, t.ClientId, t.SiteId, t.AgentId, t.Title,
            t.Description, t.Category, t.Priority, t.WorkflowStateId, t.AssignedToUserId,
            t.SlaExpiresAt, t.SlaBreached, t.CreatedAt, t.UpdatedAt, t.ClosedAt, t.DaysOpen);
    }

    public async Task<CursorPageDto<TicketCommentDto>> GetCommentsAsync(
        Guid ticketId, string? cursor, int limit, CancellationToken ct = default)
    {
        var exists = await _db.Tickets.AsNoTracking().AnyAsync(t => t.Id == ticketId, ct);
        if (!exists)
            return new CursorPageDto<TicketCommentDto>(Array.Empty<TicketCommentDto>(), 0, cursor, null, false, limit);

        var cq = _db.TicketComments.AsNoTracking().Where(c => c.TicketId == ticketId);
        if (!string.IsNullOrWhiteSpace(cursor)
            && CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var ca, out var ci))
            cq = cq.Where(c => c.CreatedAt < ca || (c.CreatedAt == ca && c.Id.CompareTo(ci) < 0));

        limit = Math.Clamp(limit, 1, 200);
        var items = await cq.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(limit + 1)
            .Select(c => new TicketCommentDto(c.Id, c.Author, c.Content, c.IsInternal, c.CreatedAt))
            .ToListAsync(ct);

        var hm = items.Count > limit;
        var pg = hm ? items.Take(limit).ToList() : items;
        var nc = hm && pg.Count > 0
            ? CursorPaginationHelper.EncodeCreatedAtCursor(pg[^1].CreatedAt, pg[^1].Id)
            : null;

        return new CursorPageDto<TicketCommentDto>(pg, pg.Count, cursor, nc, hm, limit);
    }
}
