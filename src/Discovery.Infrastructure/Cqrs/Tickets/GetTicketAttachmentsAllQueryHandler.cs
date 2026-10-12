using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Tickets;

/// <summary>
/// Anexos do chamado em UM lugar: repositório (EntityType "Ticket") + conversa
/// (EntityType "TicketComment", via comentários do chamado), com paginação por
/// cursor. É o que alimenta a aba Anexos do console e do agent.
///
/// A paginação é feita no banco (CreatedAt desc + Id desc): a versão anterior
/// carregava os comentários da primeira página e filtrava em memória, então
/// anexos de conversas antigas nunca apareciam.
/// </summary>
public sealed class GetTicketAttachmentsAllQueryHandler(
    DiscoveryDbContext db
) : IRequestHandler<GetTicketAttachmentsAllQuery, Result<CursorPageDto<TicketAttachmentEntryDto>>>
{
    private const string TicketEntityType = "Ticket";
    private const string CommentEntityType = "TicketComment";

    public async Task<Result<CursorPageDto<TicketAttachmentEntryDto>>> Handle(
        GetTicketAttachmentsAllQuery q, CancellationToken ct)
    {
        var limit = Math.Clamp(q.Limit, 1, 200);

        // Ids dos comentários do chamado: a segunda condição do filtro usa isso
        // como subconsulta traduzida pelo EF (sem trazer comentários para memória).
        var commentIds = db.TicketComments.AsNoTracking()
            .Where(c => c.TicketId == q.TicketId)
            .Select(c => c.Id);

        var query = db.Attachments.AsNoTracking()
            .Where(a => a.DeletedAt == null
                && ((a.EntityType == TicketEntityType && a.EntityId == q.TicketId)
                    || (a.EntityType == CommentEntityType && commentIds.Contains(a.EntityId))));

        if (!string.IsNullOrWhiteSpace(q.Cursor))
        {
            var parts = q.Cursor.Split('|');
            if (parts.Length == 2
                && long.TryParse(parts[0], out var ticks)
                && Guid.TryParse(parts[1], out var cursorId))
            {
                var cursorDate = new DateTime(ticks, DateTimeKind.Utc);
                query = query.Where(a => a.CreatedAt < cursorDate
                    || (a.CreatedAt == cursorDate && a.Id.CompareTo(cursorId) < 0));
            }
        }

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(limit + 1)
            .Select(a => new
            {
                a.Id,
                a.EntityType,
                a.EntityId,
                a.FileName,
                a.Description,
                a.ContentType,
                a.SizeBytes,
                a.UploadedBy,
                a.CreatedAt,
            })
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit).ToList() : rows;

        // Metadados dos comentários SÓ da página (1 consulta, sem N+1).
        var commentPageIds = page
            .Where(a => a.EntityType == CommentEntityType)
            .Select(a => a.EntityId)
            .Distinct()
            .ToList();

        var comments = commentPageIds.Count == 0
            ? new Dictionary<Guid, (string Author, DateTime? DeletedAt)>()
            : await db.TicketComments.AsNoTracking()
                .Where(c => commentPageIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Author, c.DeletedAt })
                .ToDictionaryAsync(c => c.Id, c => (c.Author, c.DeletedAt), ct);

        var items = page.Select(a =>
        {
            var isComment = a.EntityType == CommentEntityType;
            comments.TryGetValue(a.EntityId, out var comment);
            return new TicketAttachmentEntryDto(
                a.Id,
                a.FileName,
                a.Description,
                a.ContentType,
                a.SizeBytes,
                a.UploadedBy,
                a.CreatedAt,
                isComment ? "comment" : "repository",
                isComment ? a.EntityId : null,
                isComment ? comment.Author : null,
                isComment && comment.DeletedAt.HasValue);
        }).ToList().AsReadOnly();

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = $"{last.CreatedAt.Ticks}|{last.Id}";
        }

        return Result<CursorPageDto<TicketAttachmentEntryDto>>.Success(
            new CursorPageDto<TicketAttachmentEntryDto>(
                items, items.Count, q.Cursor, nextCursor, hasMore, limit));
    }
}
