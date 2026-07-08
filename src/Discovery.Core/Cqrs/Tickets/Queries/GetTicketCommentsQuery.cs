using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Tickets.Queries;

/// <summary>
/// Query to retrieve comments for a ticket with cursor pagination.
/// </summary>
public sealed record GetTicketCommentsQuery(
    Guid TicketId,
    string? Cursor,
    int Limit
) : IQuery<Result<CursorPageDto<TicketCommentDto>>>;

/// <summary>
/// Lightweight DTO for ticket comment listings.
/// </summary>
public sealed record TicketCommentDto(
    Guid Id,
    string Author,
    string Content,
    bool IsInternal,
    DateTime CreatedAt
);
