using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ITicketAlertRuleRepository
{
    Task<TicketAlertRule?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<TicketAlertRule>> GetAllAsync();
    Task<IReadOnlyList<TicketAlertRule>> GetByWorkflowStateIdAsync(Guid workflowStateId);
    Task<TicketAlertRule> CreateAsync(TicketAlertRule rule);
    Task<TicketAlertRule> UpdateAsync(TicketAlertRule rule);
    Task<bool> DeleteAsync(Guid id);
}
