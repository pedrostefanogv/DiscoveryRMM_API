namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to merge source tickets into a target ticket.
/// </summary>
public sealed record MergeTicketsCommand(
    Guid TargetTicketId,
    IReadOnlyList<Guid> SourceTicketIds,
    Guid? ChangedByUserId,
    string? Reason = null
) : ICommand<Result<MergeTicketsResult>>;

/// <summary>
/// Result of a ticket merge operation.
/// </summary>
public sealed record MergeTicketsResult(
    Guid TargetTicketId,
    int MergedCount
);