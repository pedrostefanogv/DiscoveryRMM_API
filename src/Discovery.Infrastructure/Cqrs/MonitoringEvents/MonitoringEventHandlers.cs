using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.MonitoringEvents.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.MonitoringEvents;

public sealed class ListMonitoringEventsQueryHandler() : IRequestHandler<ListMonitoringEventsQuery, Result<IReadOnlyList<MonitoringEventDto>>>
{
    public async Task<Result<IReadOnlyList<MonitoringEventDto>>> Handle(ListMonitoringEventsQuery q, CancellationToken ct)
    {
        // Delegate to service for listing — returns empty list as placeholder for now
        await Task.CompletedTask;
        return Result<IReadOnlyList<MonitoringEventDto>>.Success(Array.Empty<MonitoringEventDto>());
    }
}
