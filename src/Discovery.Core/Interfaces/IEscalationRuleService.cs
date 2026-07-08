using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IEscalationRuleService
{
    Task<IReadOnlyList<TicketEscalationRule>> GetByWorkflowProfileIdAsync(Guid workflowProfileId, CancellationToken ct = default);
    Task<TicketEscalationRule?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TicketEscalationRule> CreateAsync(TicketEscalationRule rule, CancellationToken ct = default);
    Task UpdateAsync(TicketEscalationRule rule, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
