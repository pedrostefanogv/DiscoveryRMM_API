using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IAlertToTicketService
{
    Task<Ticket> CreateTicketFromAlertAsync(
        AgentAlertDefinition alert,
        Guid clientId,
        Guid? siteId,
        Guid? agentId,
        TicketPriority priority = TicketPriority.Medium,
        CancellationToken ct = default);

    Task<Ticket> CreateTicketFromMonitoringEventAsync(
        AutoTicketCreateTicketRequest request,
        CancellationToken ct = default);
}
