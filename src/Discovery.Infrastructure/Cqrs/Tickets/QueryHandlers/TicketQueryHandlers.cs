using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class ListTicketsQueryHandler(ITicketQueryService queryService)
    : IRequestHandler<ListTicketsQuery, Result<CursorPageDto<TicketListItemDto>>>
{
    public async Task<Result<CursorPageDto<TicketListItemDto>>> Handle(ListTicketsQuery q, CancellationToken ct)
    {
        var result = await queryService.ListTicketsAsync(q.Filter, ct);
        return Result<CursorPageDto<TicketListItemDto>>.Success(result);
    }
}

public sealed class GetTicketByIdQueryHandler(ITicketQueryService queryService)
    : IRequestHandler<GetTicketByIdQuery, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(GetTicketByIdQuery q, CancellationToken ct)
    {
        var dto = await queryService.GetTicketByIdAsync(q.Id, ct);
        if (dto is null)
            return Result<TicketDetailDto>.Failure(Error.NotFound($"Ticket {q.Id} not found"));
        return Result<TicketDetailDto>.Success(dto);
    }
}

public sealed class GetTicketSlaStatusQueryHandler(ISlaService slaService)
    : IRequestHandler<GetTicketSlaStatusQuery, Result<TicketSlaStatusDto>>
{
    public async Task<Result<TicketSlaStatusDto>> Handle(GetTicketSlaStatusQuery q, CancellationToken ct)
    {
        var (hoursRemaining, percentUsed, breached) = await slaService.GetSlaStatusAsync(q.TicketId);
        return Result<TicketSlaStatusDto>.Success(new TicketSlaStatusDto(q.TicketId, null, breached, null, null, false, 0));
    }
}

public sealed class GetTicketCommentsQueryHandler(ITicketQueryService queryService)
    : IRequestHandler<GetTicketCommentsQuery, Result<CursorPageDto<TicketCommentDto>>>
{
    public async Task<Result<CursorPageDto<TicketCommentDto>>> Handle(GetTicketCommentsQuery q, CancellationToken ct)
    {
        var result = await queryService.GetCommentsAsync(q.TicketId, q.Cursor, q.Limit, ct);
        return Result<CursorPageDto<TicketCommentDto>>.Success(result);
    }
}
