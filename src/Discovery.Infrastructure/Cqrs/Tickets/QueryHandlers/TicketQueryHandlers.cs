using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class ListTicketsQueryHandler(ITicketQueryService queryService, IScopeContext scopeContext)
    : IRequestHandler<ListTicketsQuery, Result<CursorPageDto<TicketListItemDto>>>
{
    public async Task<Result<CursorPageDto<TicketListItemDto>>> Handle(ListTicketsQuery q, CancellationToken ct)
    {
        // Row-level security: injeta o ACL do usuário no filtro (mesmo padrão do Logs).
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        var filter = q.Filter with
        {
            HasGlobalAccess = access.HasGlobalAccess,
            AllowedClientIds = access.AllowedClientIds,
            AllowedSiteIds = access.AllowedSiteIds
        };

        var result = await queryService.ListTicketsAsync(filter, ct);
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
        var result = await queryService.GetCommentsAsync(q.TicketId, q.Cursor, q.Limit, q.IncludeInternal, ct);
        return Result<CursorPageDto<TicketCommentDto>>.Success(result);
    }
}
