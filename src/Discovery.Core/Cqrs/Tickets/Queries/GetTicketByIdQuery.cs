using Discovery.Core.Cqrs.Tickets.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Queries;

/// <summary>
/// Query to get a single ticket by ID.
/// </summary>
public sealed record GetTicketByIdQuery(Guid Id) : IQuery<Result<TicketDetailDto>>;