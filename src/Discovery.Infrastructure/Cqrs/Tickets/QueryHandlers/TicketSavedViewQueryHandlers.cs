using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class ListTicketSavedViewsQueryHandler(ITicketSavedViewRepository repo) : IRequestHandler<ListTicketSavedViewsQuery, Result<IReadOnlyList<TicketSavedView>>>
{ public async Task<Result<IReadOnlyList<TicketSavedView>>> Handle(ListTicketSavedViewsQuery q, CancellationToken ct) => Result<IReadOnlyList<TicketSavedView>>.Success((await repo.GetByUserAsync(q.UserId)).ToList()); }

public sealed class GetTicketSavedViewByIdQueryHandler(ITicketSavedViewRepository repo) : IRequestHandler<GetTicketSavedViewByIdQuery, Result<TicketSavedView>>
{
    public async Task<Result<TicketSavedView>> Handle(GetTicketSavedViewByIdQuery q, CancellationToken ct)
    { var v = await repo.GetByIdAsync(q.Id); return v is null ? Result<TicketSavedView>.Failure(Error.NotFound("Saved view not found.")) : Result<TicketSavedView>.Success(v); }
}
