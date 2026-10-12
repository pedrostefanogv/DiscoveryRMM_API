using Discovery.Core.Cqrs.Tickets.Queries;

namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Exclui (soft delete) um comentário do chamado. O comentário continua
/// LEGÍVEL no histórico — o conteúdo permanece — mas fica marcado como
/// excluído, e os anexos do comentário são soft-deletados junto.
/// </summary>
public sealed record DeleteTicketCommentCommand(
    Guid TicketId,
    Guid CommentId,
    Guid? UserId = null,
    string? UserName = null
) : ICommand<Result<TicketCommentDto>>;
