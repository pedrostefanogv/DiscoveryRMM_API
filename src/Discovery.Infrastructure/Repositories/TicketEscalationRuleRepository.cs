using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketEscalationRuleRepository : ITicketEscalationRuleRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketEscalationRuleRepository(DiscoveryDbContext db) => _db = db;

    public async Task<IEnumerable<TicketEscalationRule>> GetByWorkflowProfileIdAsync(Guid workflowProfileId)
        => await _db.TicketEscalationRules
            .AsNoTracking()
            .Where(r => r.WorkflowProfileId == workflowProfileId)
            .OrderBy(r => r.TriggerAtSlaPercent)
            .ToListAsync();

    public async Task<TicketEscalationRule?> GetByIdAsync(Guid id)
        => await _db.TicketEscalationRules
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == id);

    public async Task<IEnumerable<TicketEscalationRule>> GetAllActiveAsync()
        => await _db.TicketEscalationRules
            .AsNoTracking()
            .Where(r => r.IsActive)
            .ToListAsync();

    public async Task<TicketEscalationRule> CreateAsync(TicketEscalationRule rule)
    {
        rule.Id = Guid.NewGuid();
        rule.CreatedAt = DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;
        _db.TicketEscalationRules.Add(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task UpdateAsync(TicketEscalationRule rule)
    {
        rule.UpdatedAt = DateTime.UtcNow;
        _db.TicketEscalationRules.Update(rule);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
        => await _db.TicketEscalationRules
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync();
}
