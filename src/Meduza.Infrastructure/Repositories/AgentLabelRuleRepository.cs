using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AgentLabelRuleRepository : IAgentLabelRuleRepository
{
    private readonly MeduzaDbContext _db;

    public AgentLabelRuleRepository(MeduzaDbContext db) => _db = db;

    public async Task<IReadOnlyList<AgentLabelRule>> GetAllAsync(bool includeDisabled = true)
    {
        var query = _db.AgentLabelRules.AsNoTracking();
        if (!includeDisabled)
            query = query.Where(rule => rule.IsEnabled);

        return await query
            .OrderBy(rule => rule.Name)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AgentLabelRule>> GetEnabledAsync()
    {
        return await _db.AgentLabelRules
            .AsNoTracking()
            .Where(rule => rule.IsEnabled)
            .OrderBy(rule => rule.Name)
            .ToListAsync();
    }

    public async Task<AgentLabelRule?> GetByIdAsync(Guid id)
    {
        return await _db.AgentLabelRules
            .AsNoTracking()
            .SingleOrDefaultAsync(rule => rule.Id == id);
    }

    public async Task<AgentLabelRule> CreateAsync(AgentLabelRule rule)
    {
        var now = DateTime.UtcNow;
        rule.Id = IdGenerator.NewId();
        rule.CreatedAt = now;
        rule.UpdatedAt = now;

        _db.AgentLabelRules.Add(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task UpdateAsync(AgentLabelRule rule)
    {
        var existing = await _db.AgentLabelRules
            .SingleOrDefaultAsync(current => current.Id == rule.Id);

        if (existing is null)
            return;

        existing.Name = rule.Name;
        existing.Label = rule.Label;
        existing.Description = rule.Description;
        existing.IsEnabled = rule.IsEnabled;
        existing.ApplyMode = rule.ApplyMode;
        existing.ExpressionJson = rule.ExpressionJson;
        existing.UpdatedBy = rule.UpdatedBy;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.AgentLabelRules
            .Where(rule => rule.Id == id)
            .ExecuteDeleteAsync();
    }
}
