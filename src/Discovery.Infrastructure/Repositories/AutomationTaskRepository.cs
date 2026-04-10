using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AutomationTaskRepository : IAutomationTaskRepository
{
    private readonly DiscoveryDbContext _db;

    public AutomationTaskRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AutomationTaskDefinition> CreateAsync(AutomationTaskDefinition task)
    {
        task.Id = IdGenerator.NewId();
        task.CreatedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        task.LastUpdatedAt = DateTime.UtcNow;

        _db.AutomationTaskDefinitions.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    public async Task<AutomationTaskDefinition?> GetByIdAsync(Guid id, bool includeInactive = false)
    {
        var query = _db.AutomationTaskDefinitions
            .AsNoTracking()
            .Where(t => t.Id == id && t.DeletedAt == null);
        if (!includeInactive)
            query = query.Where(t => t.IsActive);

        return await query.SingleOrDefaultAsync();
    }

    public async Task<AutomationTaskDefinition?> GetByIdIncludingDeletedAsync(Guid id, bool includeInactive = false)
    {
        // includeInactive is intentionally ignored here: deleted tasks always have IsActive=false,
        // so filtering by IsActive would prevent fetching soft-deleted records.
        return await _db.AutomationTaskDefinitions
            .AsNoTracking()
            .Where(t => t.Id == id)
            .SingleOrDefaultAsync();
    }

    public async Task<IReadOnlyList<AutomationTaskDefinition>> GetListAsync(
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        bool activeOnly,
        bool deletedOnly,
        bool includeDeleted,
        string? search,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        IReadOnlyList<AppApprovalScopeType>? scopeTypes,
        IReadOnlyList<AutomationTaskActionType>? actionTypes,
        int limit,
        int offset)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var safeOffset = Math.Max(0, offset);

        var query = _db.AutomationTaskDefinitions.AsNoTracking().AsQueryable();

        if (deletedOnly)
        {
            query = query.Where(t => t.DeletedAt != null);
        }
        else if (!includeDeleted)
        {
            query = query.Where(t => t.DeletedAt == null);
        }

        if (scopeType.HasValue)
        {
            query = query.Where(t => t.ScopeType == scopeType.Value);
            if (scopeId.HasValue)
            {
                query = scopeType.Value switch
                {
                    AppApprovalScopeType.Client => query.Where(t => t.ClientId == scopeId.Value),
                    AppApprovalScopeType.Site => query.Where(t => t.SiteId == scopeId.Value),
                    AppApprovalScopeType.Agent => query.Where(t => t.AgentId == scopeId.Value),
                    _ => query
                };
            }
        }

        if (activeOnly)
            query = query.Where(t => t.IsActive);

        query = ApplyAdvancedFilters(query, search, clientId, siteId, agentId, scopeTypes, actionTypes);

        return await query
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<int> CountAsync(
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        bool activeOnly,
        bool deletedOnly,
        bool includeDeleted,
        string? search,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        IReadOnlyList<AppApprovalScopeType>? scopeTypes,
        IReadOnlyList<AutomationTaskActionType>? actionTypes)
    {
        var query = _db.AutomationTaskDefinitions.AsNoTracking().AsQueryable();

        if (deletedOnly)
        {
            query = query.Where(t => t.DeletedAt != null);
        }
        else if (!includeDeleted)
        {
            query = query.Where(t => t.DeletedAt == null);
        }

        if (scopeType.HasValue)
        {
            query = query.Where(t => t.ScopeType == scopeType.Value);
            if (scopeId.HasValue)
            {
                query = scopeType.Value switch
                {
                    AppApprovalScopeType.Client => query.Where(t => t.ClientId == scopeId.Value),
                    AppApprovalScopeType.Site => query.Where(t => t.SiteId == scopeId.Value),
                    AppApprovalScopeType.Agent => query.Where(t => t.AgentId == scopeId.Value),
                    _ => query
                };
            }
        }

        if (activeOnly)
            query = query.Where(t => t.IsActive);

        query = ApplyAdvancedFilters(query, search, clientId, siteId, agentId, scopeTypes, actionTypes);

        return await query.CountAsync();
    }

    private static IQueryable<AutomationTaskDefinition> ApplyAdvancedFilters(
        IQueryable<AutomationTaskDefinition> query,
        string? search,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        IReadOnlyList<AppApprovalScopeType>? scopeTypes,
        IReadOnlyList<AutomationTaskActionType>? actionTypes)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(t =>
                EF.Functions.ILike(t.Name, pattern)
                || (t.Description != null && EF.Functions.ILike(t.Description, pattern))
                || (t.PackageId != null && EF.Functions.ILike(t.PackageId, pattern))
                || (t.CommandPayload != null && EF.Functions.ILike(t.CommandPayload, pattern)));
        }

        if (clientId.HasValue)
            query = query.Where(t => t.ClientId == clientId.Value);

        if (siteId.HasValue)
            query = query.Where(t => t.SiteId == siteId.Value);

        if (agentId.HasValue)
            query = query.Where(t => t.AgentId == agentId.Value);

        if (scopeTypes is { Count: > 0 })
            query = query.Where(t => scopeTypes.Contains(t.ScopeType));

        if (actionTypes is { Count: > 0 })
            query = query.Where(t => actionTypes.Contains(t.ActionType));

        return query;
    }

    public async Task UpdateAsync(AutomationTaskDefinition task)
    {
        var existing = await _db.AutomationTaskDefinitions.SingleOrDefaultAsync(t => t.Id == task.Id);
        if (existing is null)
            return;

        existing.Name = task.Name;
        existing.Description = task.Description;
        existing.ActionType = task.ActionType;
        existing.InstallationType = task.InstallationType;
        existing.PackageId = task.PackageId;
        existing.ScriptId = task.ScriptId;
        existing.CommandPayload = task.CommandPayload;
        existing.ScopeType = task.ScopeType;
        existing.ClientId = task.ClientId;
        existing.SiteId = task.SiteId;
        existing.AgentId = task.AgentId;
        existing.IncludeTagsJson = task.IncludeTagsJson;
        existing.ExcludeTagsJson = task.ExcludeTagsJson;
        existing.TriggerImmediate = task.TriggerImmediate;
        existing.TriggerRecurring = task.TriggerRecurring;
        existing.TriggerOnUserLogin = task.TriggerOnUserLogin;
        existing.TriggerOnAgentCheckIn = task.TriggerOnAgentCheckIn;
        existing.ScheduleCron = task.ScheduleCron;
        existing.RequiresApproval = task.RequiresApproval;
        existing.IsActive = task.IsActive;
        existing.LastUpdatedAt = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var existing = await _db.AutomationTaskDefinitions.SingleOrDefaultAsync(t => t.Id == id && t.DeletedAt == null);
        if (existing is null)
            return;

        var now = DateTime.UtcNow;
        existing.IsActive = false;
        existing.DeletedAt = now;
        existing.LastUpdatedAt = now;
        existing.UpdatedAt = now;

        await _db.SaveChangesAsync();
    }

    public async Task<AutomationTaskDefinition?> RestoreAsync(Guid id)
    {
        var existing = await _db.AutomationTaskDefinitions.SingleOrDefaultAsync(t => t.Id == id && t.DeletedAt != null);
        if (existing is null)
            return null;

        var now = DateTime.UtcNow;
        existing.DeletedAt = null;
        existing.IsActive = true;
        existing.LastUpdatedAt = now;
        existing.UpdatedAt = now;

        await _db.SaveChangesAsync();
        return existing;
    }
}
