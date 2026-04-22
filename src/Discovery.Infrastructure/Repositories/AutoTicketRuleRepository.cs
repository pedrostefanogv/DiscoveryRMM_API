using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AutoTicketRuleRepository : IAutoTicketRuleRepository
{
    private readonly DiscoveryDbContext _db;

    public AutoTicketRuleRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AutoTicketRule?> GetByIdAsync(Guid id)
        => await _db.AutoTicketRules
            .AsNoTracking()
            .SingleOrDefaultAsync(rule => rule.Id == id);

    public async Task<IReadOnlyList<AutoTicketRule>> GetAllAsync(
        AutoTicketScopeLevel? scopeLevel = null,
        Guid? scopeId = null,
        bool? isEnabled = null,
        string? alertCode = null)
    {
        IQueryable<AutoTicketRule> query = _db.AutoTicketRules.AsNoTracking();

        if (scopeLevel.HasValue)
            query = query.Where(rule => rule.ScopeLevel == scopeLevel.Value);

        if (scopeId.HasValue)
            query = query.Where(rule => rule.ScopeId == scopeId.Value);

        if (isEnabled.HasValue)
            query = query.Where(rule => rule.IsEnabled == isEnabled.Value);

        if (!string.IsNullOrWhiteSpace(alertCode))
        {
            var normalized = alertCode.Trim();
            query = query.Where(rule => rule.AlertCodeFilter != null && EF.Functions.ILike(rule.AlertCodeFilter, normalized));
        }

        return await query
            .OrderByDescending(rule => rule.PriorityOrder)
            .ThenByDescending(rule => rule.CreatedAt)
            .ToListAsync();
    }

    public async Task<AutoTicketRule> CreateAsync(AutoTicketRule rule)
    {
        rule.Id = rule.Id == Guid.Empty ? IdGenerator.NewId() : rule.Id;
        rule.CreatedAt = DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;
        _db.AutoTicketRules.Add(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task<AutoTicketRule> UpdateAsync(AutoTicketRule rule)
    {
        rule.UpdatedAt = DateTime.UtcNow;
        _db.AutoTicketRules.Update(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var rule = await _db.AutoTicketRules.SingleOrDefaultAsync(existing => existing.Id == id);
        if (rule is null)
            return false;

        _db.AutoTicketRules.Remove(rule);
        await _db.SaveChangesAsync();
        return true;
    }
}