using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(Guid id);
    Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null);
    Task<IEnumerable<Ticket>> GetAllAsync(Guid? workflowStateId = null, int limit = 100, int offset = 0);
    Task<Ticket> CreateAsync(Ticket ticket);
    Task UpdateAsync(Ticket ticket);
    Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId);
    Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId);
    Task<TicketComment> AddCommentAsync(TicketComment comment);
}
