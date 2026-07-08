using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class EscalationRuleService : IEscalationRuleService
{
    private readonly ITicketEscalationRuleRepository _repo;
    public EscalationRuleService(ITicketEscalationRuleRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<TicketEscalationRule>> GetByWorkflowProfileIdAsync(Guid workflowProfileId, CancellationToken ct = default)
    { var items = await _repo.GetByWorkflowProfileIdAsync(workflowProfileId); return items.ToList().AsReadOnly(); }
    public Task<TicketEscalationRule?> GetByIdAsync(Guid id, CancellationToken ct = default) => _repo.GetByIdAsync(id);
    public Task<TicketEscalationRule> CreateAsync(TicketEscalationRule rule, CancellationToken ct = default) => _repo.CreateAsync(rule);
    public Task UpdateAsync(TicketEscalationRule rule, CancellationToken ct = default) => _repo.UpdateAsync(rule);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _repo.DeleteAsync(id);
}
