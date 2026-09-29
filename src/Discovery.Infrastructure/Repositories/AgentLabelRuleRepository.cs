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

    public async Task UpsertRangeAsync(IReadOnlyCollection<AgentLabelRule> rules, CancellationToken ct = default)
    {
        if (rules.Count == 0)
            return;

        var ids = rules.Select(rule => rule.Id).ToList();
        var existing = await _db.AgentLabelRules
            .Where(rule => ids.Contains(rule.Id))
            .ToDictionaryAsync(rule => rule.Id, ct);

        var now = DateTime.UtcNow;
        foreach (var rule in rules)
        {
            if (existing.TryGetValue(rule.Id, out var current))
            {
                Apply(current, rule);
                current.UpdatedAt = now;
                continue;
            }

            rule.CreatedAt = rule.CreatedAt == default ? now : rule.CreatedAt;
            rule.UpdatedAt = now;
            _db.AgentLabelRules.Add(rule);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.AgentLabelRules
            .Where(rule => rule.Id == id)
            .ExecuteDeleteAsync();
    }

    public async Task AddVersionAsync(AgentLabelRule rule, string? changedBy, CancellationToken ct = default)
    {
        _db.AgentLabelRuleVersions.Add(ToVersion(rule, changedBy));
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddVersionsAsync(IReadOnlyCollection<AgentLabelRule> rules, CancellationToken ct = default)
    {
        if (rules.Count == 0)
            return;

        _db.AgentLabelRuleVersions.AddRange(
            rules.Select(rule => ToVersion(rule, rule.UpdatedBy ?? rule.CreatedBy)));

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentLabelRuleVersion>> GetVersionsAsync(Guid ruleId, int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);

        return await _db.AgentLabelRuleVersions
            .AsNoTracking()
            .Where(version => version.RuleId == ruleId)
            .OrderByDescending(version => version.ChangedAt)
            .ThenByDescending(version => version.Id)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    private static AgentLabelRuleVersion ToVersion(AgentLabelRule rule, string? changedBy) => new()
    {
        Id = IdGenerator.NewId(),
        RuleId = rule.Id,
        Name = rule.Name,
        Label = rule.Label,
        Description = rule.Description,
        IsEnabled = rule.IsEnabled,
        ApplyMode = rule.ApplyMode,
        ExpressionJson = rule.ExpressionJson,
        ChangedBy = changedBy,
        ChangedAt = DateTime.UtcNow
    };
}
