using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Tickets.Queries;

/// <summary>
/// Query to list tickets with filtering and cursor-based pagination.
/// </summary>
public sealed record ListTicketsQuery(
    TicketFilterQuery Filter
) : IQuery<Result<CursorPageDto<TicketListItemDto>>>;
