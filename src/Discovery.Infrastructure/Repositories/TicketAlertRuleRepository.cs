using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketAlertRuleRepository : ITicketAlertRuleRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketAlertRuleRepository(DiscoveryDbContext db) => _db = db;

    public async Task<TicketAlertRule?> GetByIdAsync(Guid id)
        => await _db.TicketAlertRules.FindAsync(id);

    public async Task<IReadOnlyList<TicketAlertRule>> GetAllAsync()
        => await _db.TicketAlertRules.OrderBy(r => r.CreatedAt).ToListAsync();

    public async Task<IReadOnlyList<TicketAlertRule>> GetByWorkflowStateIdAsync(Guid workflowStateId)
        => await _db.TicketAlertRules
            .Where(r => r.WorkflowStateId == workflowStateId && r.IsEnabled)
            .ToListAsync();

    public async Task<TicketAlertRule> CreateAsync(TicketAlertRule rule)
    {
        rule.Id = Guid.NewGuid();
        rule.CreatedAt = DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;
        _db.TicketAlertRules.Add(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task<TicketAlertRule> UpdateAsync(TicketAlertRule rule)
    {
        rule.UpdatedAt = DateTime.UtcNow;
        _db.TicketAlertRules.Update(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var rule = await _db.TicketAlertRules.FindAsync(id);
        if (rule is null) return false;
        _db.TicketAlertRules.Remove(rule);
        await _db.SaveChangesAsync();
        return true;
    }
}
