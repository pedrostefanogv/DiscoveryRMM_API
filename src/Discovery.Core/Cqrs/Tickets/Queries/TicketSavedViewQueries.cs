using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Tickets.Queries;

public sealed record ListTicketSavedViewsQuery(Guid? UserId, bool HasGlobalAccess = true, IReadOnlyCollection<Guid>? AllowedClientIds = null) : IQuery<Result<IReadOnlyList<TicketSavedView>>>;
public sealed record GetTicketSavedViewByIdQuery(Guid Id) : IQuery<Result<TicketSavedView>>;
