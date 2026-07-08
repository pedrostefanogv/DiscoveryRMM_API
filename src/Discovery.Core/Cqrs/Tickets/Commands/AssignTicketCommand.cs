namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to assign a ticket to a user.
/// </summary>
public sealed record AssignTicketCommand(
    Guid TicketId,
    Guid? AssignedToUserId,
    Guid? ChangedByUserId
) : ICommand<Result<AssignTicketResult>>;

/// <summary>
/// Result of assigning a ticket.
/// </summary>
public sealed record AssignTicketResult(
    Guid TicketId,
    Guid? AssignedToUserId
);