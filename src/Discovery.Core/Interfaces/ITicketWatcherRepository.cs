using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ITicketWatcherRepository
{
    Task<IEnumerable<TicketWatcher>> GetByTicketAsync(Guid ticketId);
    Task<TicketWatcher?> GetAsync(Guid ticketId, Guid userId);
    Task<TicketWatcher> AddAsync(Guid ticketId, Guid userId, string? addedBy);
    Task RemoveAsync(Guid ticketId, Guid userId);
}
