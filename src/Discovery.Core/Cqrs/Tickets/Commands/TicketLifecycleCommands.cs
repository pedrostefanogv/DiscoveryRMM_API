using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>Reabre um chamado encerrado: volta ao estado inicial e recalcula o SLA.</summary>
public sealed record ReopenTicketCommand(Guid TicketId, string? Reason, Guid? ChangedByUserId)
    : ICommand<Result<TicketDetailDto>>;

/// <summary>Avalia (CSAT 1..5) um chamado encerrado. Upsert nos campos do próprio chamado.</summary>
public sealed record RateTicketCommand(
    Guid TicketId,
    int Rating,
    string? Feedback,
    Guid? RatedByUserId,
    string? RatedByName
) : ICommand<Result<TicketDetailDto>>;
