using Discovery.Core.Cqrs.Tickets.Events;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Tickets.EventHandlers;

public sealed class TicketCreatedLogHandler(
    ILogger<TicketCreatedLogHandler> logger,
    ITicketKpiCacheService? kpiCache = null) : INotificationHandler<TicketCreatedEvent>
{
    public async Task Handle(TicketCreatedEvent e, CancellationToken ct)
    {
        logger.LogInformation("Ticket {Id}: {Title}", e.TicketId, e.Title);
        if (kpiCache is not null)
            await kpiCache.InvalidateAsync(e.ClientId, ct);
    }
}

public sealed class TicketStateChangedLogHandler(
    ILogger<TicketStateChangedLogHandler> logger,
    ITicketKpiCacheService? kpiCache = null) : INotificationHandler<TicketStateChangedEvent>
{
    public async Task Handle(TicketStateChangedEvent e, CancellationToken ct)
    {
        logger.LogInformation("Ticket {Id}: {Prev}->{New}", e.TicketId, e.PreviousStateId, e.NewStateId);
        if (kpiCache is not null)
            await kpiCache.InvalidateAllAsync(ct);
    }
}

public sealed class SlaBreachLogHandler(
    ILogger<SlaBreachLogHandler> logger,
    ITicketKpiCacheService? kpiCache = null) : INotificationHandler<SlaBreachEvent>
{
    public async Task Handle(SlaBreachEvent e, CancellationToken ct)
    {
        logger.LogWarning("SLA breached: ticket {Id}", e.TicketId);
        if (kpiCache is not null)
            await kpiCache.InvalidateAllAsync(ct);
    }
}
