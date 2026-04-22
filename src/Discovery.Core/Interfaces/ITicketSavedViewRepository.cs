using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ITicketSavedViewRepository
{
    Task<IEnumerable<TicketSavedView>> GetByUserAsync(Guid? userId);
    Task<TicketSavedView?> GetByIdAsync(Guid id);
    Task<TicketSavedView> CreateAsync(TicketSavedView view);
    Task UpdateAsync(TicketSavedView view);
    Task DeleteAsync(Guid id);
}
