using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.SlaCalendars.Commands;

namespace Discovery.Core.Cqrs.SlaCalendars.Queries;

public sealed record ListSlaCalendarsQuery(Guid? ClientId) : IQuery<Result<IReadOnlyList<SlaCalendarDto>>>;
public sealed record GetSlaCalendarByIdQuery(Guid Id) : IQuery<Result<SlaCalendarDto>>;