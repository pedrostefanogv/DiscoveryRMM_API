using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(Guid id);
    Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null);
    Task<IEnumerable<Ticket>> GetByAgentIdAsync(Guid agentId, Guid? workflowStateId = null);
    Task<IEnumerable<Ticket>> GetAllAsync(Guid? workflowStateId = null, int limit = 100, int offset = 0);
    Task<Ticket> CreateAsync(Ticket ticket);
    Task UpdateAsync(Ticket ticket);
    Task DeleteAsync(Guid id);
    Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId);
    Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId);
    Task<TicketComment> AddCommentAsync(TicketComment comment);
    
    /// <summary>
    /// Obtém todos os tickets abertos (não fechados) que possuem SLA configurado.
    /// Usado pelo SLA Monitoring Background Service.
    /// </summary>
    Task<List<Ticket>> GetOpenTicketsWithSlaAsync();
}
