using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AutomationTaskRepository : IAutomationTaskRepository
{
    private readonly MeduzaDbContext _db;

    public AutomationTaskRepository(MeduzaDbContext db) => _db = db;

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
        var query = _db.AutomationTaskDefinitions.AsNoTracking().Where(t => t.Id == id);
        if (!includeInactive)
            query = query.Where(t => t.IsActive);

        return await query.SingleOrDefaultAsync();
    }

    public async Task<IReadOnlyList<AutomationTaskDefinition>> GetListAsync(AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly, int limit, int offset)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var safeOffset = Math.Max(0, offset);

        var query = _db.AutomationTaskDefinitions.AsNoTracking().AsQueryable();

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

        return await query
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<int> CountAsync(AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly)
    {
        var query = _db.AutomationTaskDefinitions.AsNoTracking().AsQueryable();

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

        return await query.CountAsync();
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
        await _db.AutomationTaskDefinitions
            .Where(t => t.Id == id)
            .ExecuteDeleteAsync();
    }
}
