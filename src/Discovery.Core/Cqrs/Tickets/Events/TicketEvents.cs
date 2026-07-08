using MediatR;

namespace Discovery.Core.Cqrs.Tickets.Events;

/// <summary>
/// Event raised when a new ticket is created.
/// </summary>
public sealed record TicketCreatedEvent(
    Guid TicketId,
    string Title,
    Guid ClientId,
    Guid? SiteId,
    Guid? AssignedToUserId,
    DateTime CreatedAt
) : INotification;

/// <summary>
/// Event raised when a ticket's workflow state changes.
/// </summary>
public sealed record TicketStateChangedEvent(
    Guid TicketId,
    Guid PreviousStateId,
    Guid NewStateId,
    Guid? ChangedByUserId,
    DateTime Timestamp
) : INotification;

/// <summary>
/// Event raised when a ticket's SLA is breached.
/// </summary>
public sealed record SlaBreachEvent(
    Guid TicketId,
    DateTime BreachedAt
) : INotification;

/// <summary>
/// Event raised when a ticket is assigned to a user.
/// </summary>
public sealed record TicketAssignedEvent(
    Guid TicketId,
    Guid? PreviousAssigneeId,
    Guid? NewAssigneeId,
    DateTime Timestamp
) : INotification;

/// <summary>
/// Event raised when a comment is added to a ticket.
/// </summary>
public sealed record TicketCommentAddedEvent(
    Guid TicketId,
    Guid CommentId,
    string Content,
    bool IsInternal,
    Guid? UserId,
    DateTime Timestamp
) : INotification;