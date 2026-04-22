using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ITicketEscalationRuleRepository
{
    Task<IEnumerable<TicketEscalationRule>> GetByWorkflowProfileIdAsync(Guid workflowProfileId);
    Task<TicketEscalationRule?> GetByIdAsync(Guid id);
    Task<TicketEscalationRule> CreateAsync(TicketEscalationRule rule);
    Task UpdateAsync(TicketEscalationRule rule);
    Task DeleteAsync(Guid id);
    Task<IEnumerable<TicketEscalationRule>> GetAllActiveAsync();
}
