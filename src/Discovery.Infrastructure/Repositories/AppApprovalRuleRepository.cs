using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AppApprovalRuleRepository : IAppApprovalRuleRepository
{
    private readonly DiscoveryDbContext _db;

    public AppApprovalRuleRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AppApprovalRule?> GetByIdAsync(Guid id)
    {
        return await _db.AppApprovalRules
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<IReadOnlyList<AppApprovalRule>> GetByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType)
    {
        var query = _db.AppApprovalRules
            .AsNoTracking()
            .Where(x => x.ScopeType == scopeType && x.InstallationType == installationType);

        query = scopeType switch
        {
            AppApprovalScopeType.Global => query.Where(x => x.ClientId == null && x.SiteId == null && x.AgentId == null),
            AppApprovalScopeType.Client => query.Where(x => x.ClientId == scopeId),
            AppApprovalScopeType.Site => query.Where(x => x.SiteId == scopeId),
            AppApprovalScopeType.Agent => query.Where(x => x.AgentId == scopeId),
            _ => query
        };

        return await query
            .OrderBy(x => x.PackageId)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AppApprovalRule>> GetForInheritanceAsync(Guid clientId, Guid siteId, Guid agentId, AppInstallationType installationType)
    {
        return await _db.AppApprovalRules
            .AsNoTracking()
            .Where(x => x.InstallationType == installationType)
            .Where(x =>
                (x.ScopeType == AppApprovalScopeType.Global && x.ClientId == null && x.SiteId == null && x.AgentId == null) ||
                (x.ScopeType == AppApprovalScopeType.Client && x.ClientId == clientId) ||
                (x.ScopeType == AppApprovalScopeType.Site && x.SiteId == siteId) ||
                (x.ScopeType == AppApprovalScopeType.Agent && x.AgentId == agentId))
            .OrderBy(x => x.PackageId)
            .ThenBy(x => x.ScopeType)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync();
    }

    public async Task<AppApprovalRule?> GetByUniqueKeyAsync(
        AppApprovalScopeType scopeType,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType,
        string packageId)
    {
        return await _db.AppApprovalRules
            .SingleOrDefaultAsync(x =>
                x.ScopeType == scopeType &&
                x.ClientId == clientId &&
                x.SiteId == siteId &&
                x.AgentId == agentId &&
                x.InstallationType == installationType &&
                x.PackageId == packageId);
    }

    public async Task<AppApprovalRule> CreateAsync(AppApprovalRule rule)
    {
        rule.Id = IdGenerator.NewId();
        rule.CreatedAt = DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;
        _db.AppApprovalRules.Add(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task UpdateAsync(AppApprovalRule rule)
    {
        var existing = await _db.AppApprovalRules.SingleOrDefaultAsync(x => x.Id == rule.Id);
        if (existing is null)
            return;

        existing.Action = rule.Action;
        existing.AutoUpdateEnabled = rule.AutoUpdateEnabled;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.AppApprovalRules
            .Where(x => x.Id == id)
            .ExecuteDeleteAsync();
    }
}
