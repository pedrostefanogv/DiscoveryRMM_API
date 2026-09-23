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

public sealed class GetTicketSlaStatusQueryHandler(ISlaService slaService, ITicketRepository ticketRepo)
    : IRequestHandler<GetTicketSlaStatusQuery, Result<TicketSlaStatusDto>>
{
    public async Task<Result<TicketSlaStatusDto>> Handle(GetTicketSlaStatusQuery q, CancellationToken ct)
    {
        // Antes o handler descartava horas/percentual e devolvia o DTO com
        // expiração/pausa nulas — o endpoint /sla/status vinha vazio.
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        if (ticket is null)
            return Result<TicketSlaStatusDto>.Failure(Error.NotFound($"Ticket {q.TicketId} not found"));

        var (_, _, computedBreached) = await slaService.GetSlaStatusAsync(q.TicketId);

        return Result<TicketSlaStatusDto>.Success(new TicketSlaStatusDto(
            q.TicketId,
            ticket.SlaExpiresAt,
            ticket.SlaBreached || computedBreached,
            ticket.SlaFirstResponseExpiresAt,
            ticket.FirstRespondedAt,
            ticket.SlaHoldStartedAt.HasValue,
            ticket.SlaPausedSeconds));
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
