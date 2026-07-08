namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to add a comment to a ticket.
/// </summary>
public sealed record AddTicketCommentCommand(
    Guid TicketId,
    string Content,
    bool IsInternal,
    Guid? UserId,
    string? UserName
) : ICommand<Result<AddTicketCommentResult>>;

/// <summary>
/// Result of adding a ticket comment.
/// </summary>
public sealed record AddTicketCommentResult(
    Guid CommentId,
    DateTime CreatedAt
);