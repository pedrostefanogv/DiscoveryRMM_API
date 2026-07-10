using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets;

/// <summary>
/// Handler para listar anexos de um ticket com paginação por cursor.
/// </summary>
public sealed class GetTicketAttachmentsQueryHandler(
    IAttachmentRepository attachmentRepo
) : IRequestHandler<GetTicketAttachmentsQuery, Result<CursorPageDto<TicketAttachmentDto>>>
{
    private const string TicketEntityType = "Ticket";

    public async Task<Result<CursorPageDto<TicketAttachmentDto>>> Handle(
        GetTicketAttachmentsQuery q, CancellationToken ct)
    {
        var allAttachments = await attachmentRepo.GetByEntityAsync(TicketEntityType, q.TicketId, ct);

        // Ordenação: mais recentes primeiro
        var ordered = allAttachments
            .Where(a => !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToList();

        // Aplicar cursor se presente
        if (!string.IsNullOrWhiteSpace(q.Cursor))
        {
            var parts = q.Cursor.Split('|');
            if (parts.Length == 2
                && long.TryParse(parts[0], out var ticks)
                && Guid.TryParse(parts[1], out var cursorId))
            {
                var cursorDate = new DateTime(ticks, DateTimeKind.Utc);
                ordered = ordered
                    .Where(a => a.CreatedAt < cursorDate
                        || (a.CreatedAt == cursorDate && a.Id.CompareTo(cursorId) < 0))
                    .ToList();
            }
        }

        var limit = Math.Clamp(q.Limit, 1, 100);
        var hasMore = ordered.Count > limit;
        var page = ordered.Take(limit).ToList();

        var items = page.Select(a => new TicketAttachmentDto(
            a.Id,
            a.FileName,
            a.Description,
            a.ContentType,
            a.SizeBytes,
            a.UploadedBy,
            a.CreatedAt
        )).ToList().AsReadOnly();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = page[^1];
            nextCursor = $"{last.CreatedAt.Ticks}|{last.Id}";
        }

        return Result<CursorPageDto<TicketAttachmentDto>>.Success(
            new CursorPageDto<TicketAttachmentDto>(items, items.Count, q.Cursor, nextCursor, hasMore, q.Limit));
    }
}
