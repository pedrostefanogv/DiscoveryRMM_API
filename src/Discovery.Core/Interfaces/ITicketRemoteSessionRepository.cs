using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ITicketRemoteSessionRepository
{
    Task<IEnumerable<TicketRemoteSession>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default);
    Task<TicketRemoteSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TicketRemoteSession> CreateAsync(TicketRemoteSession session, CancellationToken ct = default);
    Task<TicketRemoteSession> UpdateAsync(TicketRemoteSession session, CancellationToken ct = default);
}
