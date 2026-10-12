using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

/// <summary>
/// Soft delete de comentário de chamado.
///
/// Decisões de produto:
/// - o comentário NÃO é removido da tabela: o conteúdo permanece legível e o
///   chamado continua auditável (marcado como excluído no histórico);
/// - os anexos do comentário são soft-deletados junto (saem da listagem, mas
///   seguem no storage até a limpeza de retenção);
/// - a exclusão é idempotente (não regrava data nem duplica evento).
/// </summary>
public sealed class DeleteTicketCommentCommandHandler(
    DiscoveryDbContext db,
    IAttachmentRepository attachmentRepository,
    IActivityLogService activityLog
) : IRequestHandler<DeleteTicketCommentCommand, Result<TicketCommentDto>>
{
    private const string CommentEntityType = "TicketComment";

    public async Task<Result<TicketCommentDto>> Handle(DeleteTicketCommentCommand cmd, CancellationToken ct)
    {
        var comment = await db.TicketComments
            .FirstOrDefaultAsync(c => c.Id == cmd.CommentId && c.TicketId == cmd.TicketId, ct);

        if (comment is null)
            return Result<TicketCommentDto>.Failure(Error.NotFound("Comment not found"));

        if (!comment.IsDeleted)
        {
            var now = DateTime.UtcNow;
            comment.DeletedAt = now;
            comment.DeletedBy = string.IsNullOrWhiteSpace(cmd.UserName) ? null : cmd.UserName;
            await db.SaveChangesAsync(ct);

            await attachmentRepository.SoftDeleteByEntityAsync(CommentEntityType, comment.Id, ct);

            await activityLog.LogActivityAsync(
                cmd.TicketId,
                TicketActivityType.CommentDeleted,
                cmd.UserId,
                oldValue: null,
                newValue: null,
                comment: "Comentário excluído (conteúdo preservado para leitura).");
        }

        return Result<TicketCommentDto>.Success(ToDto(comment));
    }

    private static TicketCommentDto ToDto(Discovery.Core.Entities.TicketComment comment) => new(
        comment.Id,
        comment.Author,
        comment.Content,
        comment.IsInternal,
        comment.CreatedAt,
        comment.DeletedAt,
        comment.DeletedBy,
        null);
}
