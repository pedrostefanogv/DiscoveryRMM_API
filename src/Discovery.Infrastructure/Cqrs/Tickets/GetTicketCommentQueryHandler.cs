using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Tickets;

/// <summary>
/// Devolve UM comentário do chamado (com anexos do nível conversa). O filtro
/// por TicketId é obrigatório: sem ele, um comentário de outro chamado seria
/// aceito como pai de um anexo (IDOR).
/// </summary>
public sealed class GetTicketCommentQueryHandler(
    DiscoveryDbContext db
) : IRequestHandler<GetTicketCommentQuery, Result<TicketCommentDto>>
{
    private const string CommentEntityType = "TicketComment";

    public async Task<Result<TicketCommentDto>> Handle(GetTicketCommentQuery q, CancellationToken ct)
    {
        var comment = await db.TicketComments.AsNoTracking()
            .Where(c => c.Id == q.CommentId && c.TicketId == q.TicketId)
            .Select(c => new { c.Id, c.Author, c.Content, c.IsInternal, c.CreatedAt, c.DeletedAt, c.DeletedBy })
            .FirstOrDefaultAsync(ct);

        if (comment is null)
            return Result<TicketCommentDto>.Failure(Error.NotFound("Comment not found"));

        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.EntityType == CommentEntityType
                && a.EntityId == q.CommentId
                && a.DeletedAt == null)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new TicketCommentAttachmentDto(
                a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedBy, a.CreatedAt))
            .ToListAsync(ct);

        return Result<TicketCommentDto>.Success(new TicketCommentDto(
            comment.Id,
            comment.Author,
            comment.Content,
            comment.IsInternal,
            comment.CreatedAt,
            comment.DeletedAt,
            comment.DeletedBy,
            attachments));
    }
}
