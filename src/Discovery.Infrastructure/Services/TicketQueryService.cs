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
        if (f.WorkflowProfileId.HasValue) query = query.Where(t => t.WorkflowProfileId == f.WorkflowProfileId.Value);
        if (f.AssignedToUserId.HasValue) query = query.Where(t => t.AssignedToUserId == f.AssignedToUserId.Value);
        if (f.Priority.HasValue) query = query.Where(t => t.Priority == f.Priority.Value);
        if (f.SlaBreached.HasValue) query = query.Where(t => t.SlaBreached == f.SlaBreached.Value);
        if (f.IsClosed.HasValue)
            query = f.IsClosed.Value
                ? query.Where(t => t.ClosedAt != null)
                : query.Where(t => t.ClosedAt == null);

        // Row-level security (mesmo padrão de TicketRepository.GetAllPageAsync/LogRepository).
        if (!f.HasGlobalAccess)
        {
            var allowedClientIds = (f.AllowedClientIds ?? []).Distinct().ToArray();
            var allowedSiteIds = (f.AllowedSiteIds ?? []).Distinct().ToArray();
            if (allowedClientIds.Length == 0 && allowedSiteIds.Length == 0)
            {
                var emptyLimit = Math.Clamp(f.Limit, 1, 200);
                return new CursorPageDto<TicketListItemDto>(Array.Empty<TicketListItemDto>(), 0, f.Cursor, null, false, emptyLimit);
            }

            query = query.Where(t =>
                allowedClientIds.Contains(t.ClientId) ||
                (t.SiteId.HasValue && allowedSiteIds.Contains(t.SiteId.Value)));
        }

        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            // Escapa curingas do LIKE para "50%" não virar qualquer-coisa.
            var term = f.Text.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            var pattern = $"%{term}%";
            query = query.Where(t =>
                EF.Functions.ILike(t.Title, pattern) || EF.Functions.ILike(t.Description, pattern));
        }
        // O cursor é Base64 "ticks|guidN" (EncodeCreatedAtCursor). O código antigo
        // tentava Guid.TryParse direto no Base64 e sempre falhava → paginação
        // regressava para a primeira página.
        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(f.Cursor, out var cursorCreatedAt, out var cursorId))
            query = CursorPaginationHelper.ApplyCreatedAtCursor(query, cursorCreatedAt, cursorId, t => t.CreatedAt, t => t.Id);

        var limit = Math.Clamp(f.Limit, 1, 200);
        var items = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(limit + 1)
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
            t.SlaExpiresAt, t.SlaBreached, t.CreatedAt, t.UpdatedAt, t.ClosedAt, t.DaysOpen,
            t.Rating, t.RatingFeedback, t.RatedAt, t.RatedBy, t.SubmissionSnapshotMarkdown);
    }

    public async Task<CursorPageDto<TicketCommentDto>> GetCommentsAsync(
        Guid ticketId, string? cursor, int limit, bool includeInternal = false, CancellationToken ct = default)
    {
        var exists = await _db.Tickets.AsNoTracking().AnyAsync(t => t.Id == ticketId, ct);
        if (!exists)
            return new CursorPageDto<TicketCommentDto>(Array.Empty<TicketCommentDto>(), 0, cursor, null, false, limit);

        var cq = _db.TicketComments.AsNoTracking().Where(c => c.TicketId == ticketId);
        // Notas internas: visíveis somente para quem pode editar o chamado.
        if (!includeInternal)
            cq = cq.Where(c => !c.IsInternal);
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
