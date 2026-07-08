namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to transition a ticket to a new workflow state.
/// </summary>
public sealed record TransitionTicketStateCommand(
    Guid TicketId,
    Guid TargetStateId,
    Guid? ChangedByUserId
) : ICommand<Result<TransitionTicketStateResult>>;

/// <summary>
/// Result of a ticket state transition.
/// </summary>
public sealed record TransitionTicketStateResult(
    Guid TicketId,
    Guid PreviousStateId,
    Guid NewStateId,
    DateTime? ClosedAt
);