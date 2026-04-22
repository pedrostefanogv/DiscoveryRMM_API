using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ITicketAutomationLinkRepository
{
    Task<IReadOnlyList<TicketAutomationLink>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default);
    Task<TicketAutomationLink?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TicketAutomationLink> CreateAsync(TicketAutomationLink link, CancellationToken ct = default);
    Task UpdateAsync(TicketAutomationLink link, CancellationToken ct = default);
}
