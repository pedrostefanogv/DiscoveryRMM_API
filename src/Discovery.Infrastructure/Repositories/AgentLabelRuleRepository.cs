using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentLabelRuleRepository : IAgentLabelRuleRepository
{
    private readonly DiscoveryDbContext _db;

    public AgentLabelRuleRepository(DiscoveryDbContext db) => _db = db;

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

        Apply(existing, rule);
        existing.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Concorrencia otimista (xmin): outro processo alterou a regra entre a
            // leitura e a escrita. Antes isso era um lost update silencioso; agora a
            // disputa e detectada. Recarrega e reaplica uma vez para convergir, mas
            // preserva o UpdatedBy/UpdatedAt do autor mais recente.
            await _db.Entry(existing).ReloadAsync();
            Apply(existing, rule);
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    private static void Apply(AgentLabelRule target, AgentLabelRule source)
    {
        target.Name = source.Name;
        target.Label = source.Label;
        target.Description = source.Description;
        target.IsEnabled = source.IsEnabled;
        target.ApplyMode = source.ApplyMode;
        target.ExpressionJson = source.ExpressionJson;
        target.UpdatedBy = source.UpdatedBy;
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.AgentLabelRules
            .Where(rule => rule.Id == id)
            .ExecuteDeleteAsync();
    }
}
