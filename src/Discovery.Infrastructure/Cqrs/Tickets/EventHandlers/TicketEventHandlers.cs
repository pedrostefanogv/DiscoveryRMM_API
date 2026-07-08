using Discovery.Core.Cqrs.Tickets.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Tickets.EventHandlers;

public sealed class TicketCreatedLogHandler(ILogger<TicketCreatedLogHandler> logger) : INotificationHandler<TicketCreatedEvent>
{ public Task Handle(TicketCreatedEvent e, CancellationToken ct) { logger.LogInformation("Ticket {Id}: {Title}", e.TicketId, e.Title); return Task.CompletedTask; } }

public sealed class TicketStateChangedLogHandler(ILogger<TicketStateChangedLogHandler> logger) : INotificationHandler<TicketStateChangedEvent>
{ public Task Handle(TicketStateChangedEvent e, CancellationToken ct) { logger.LogInformation("Ticket {Id}: {Prev}->{New}", e.TicketId, e.PreviousStateId, e.NewStateId); return Task.CompletedTask; } }

public sealed class SlaBreachLogHandler(ILogger<SlaBreachLogHandler> logger) : INotificationHandler<SlaBreachEvent>
{ public Task Handle(SlaBreachEvent e, CancellationToken ct) { logger.LogWarning("SLA breached: ticket {Id}", e.TicketId); return Task.CompletedTask; } }
