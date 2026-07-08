using Discovery.Core.Cqrs.Tickets.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Queries;

/// <summary>
/// Query to get SLA status for a ticket.
/// </summary>
public sealed record GetTicketSlaStatusQuery(Guid TicketId) : IQuery<Result<TicketSlaStatusDto>>;

/// <summary>
/// DTO for ticket SLA status information.
/// </summary>
public sealed record TicketSlaStatusDto(
    Guid TicketId,
    DateTime? SlaExpiresAt,
    bool SlaBreached,
    DateTime? SlaFirstResponseExpiresAt,
    DateTime? FirstRespondedAt,
    bool IsPaused,
    int SlaPausedSeconds
);