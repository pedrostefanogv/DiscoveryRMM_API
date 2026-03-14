using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AutomationScriptRepository : IAutomationScriptRepository
{
    private readonly MeduzaDbContext _db;

    public AutomationScriptRepository(MeduzaDbContext db) => _db = db;

    public async Task<AutomationScriptDefinition> CreateAsync(AutomationScriptDefinition script)
    {
        script.Id = IdGenerator.NewId();
        script.CreatedAt = DateTime.UtcNow;
        script.UpdatedAt = DateTime.UtcNow;
        script.LastUpdatedAt = DateTime.UtcNow;

        _db.AutomationScriptDefinitions.Add(script);
        await _db.SaveChangesAsync();

        return script;
    }

    public async Task<AutomationScriptDefinition?> GetByIdAsync(Guid id, bool includeInactive = false)
    {
        var query = _db.AutomationScriptDefinitions.AsNoTracking().Where(script => script.Id == id);
        if (!includeInactive)
            query = query.Where(script => script.IsActive);

        return await query.SingleOrDefaultAsync();
    }

    public async Task<IReadOnlyList<AutomationScriptDefinition>> GetListAsync(Guid? clientId, bool activeOnly, int limit, int offset)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var safeOffset = Math.Max(0, offset);

        var query = _db.AutomationScriptDefinitions.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
            query = query.Where(script => script.ClientId == clientId.Value || script.ClientId == null);

        if (activeOnly)
            query = query.Where(script => script.IsActive);

        return await query
            .OrderByDescending(script => script.UpdatedAt)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<int> CountAsync(Guid? clientId, bool activeOnly)
    {
        var query = _db.AutomationScriptDefinitions.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
            query = query.Where(script => script.ClientId == clientId.Value || script.ClientId == null);

        if (activeOnly)
            query = query.Where(script => script.IsActive);

        return await query.CountAsync();
    }

    public async Task UpdateAsync(AutomationScriptDefinition script)
    {
        var existing = await _db.AutomationScriptDefinitions.SingleOrDefaultAsync(x => x.Id == script.Id);
        if (existing is null)
            return;

        existing.Name = script.Name;
        existing.Summary = script.Summary;
        existing.ScriptType = script.ScriptType;
        existing.Version = script.Version;
        existing.ExecutionFrequency = script.ExecutionFrequency;
        existing.TriggerModesJson = script.TriggerModesJson;
        existing.Content = script.Content;
        existing.ParametersSchemaJson = script.ParametersSchemaJson;
        existing.MetadataJson = script.MetadataJson;
        existing.IsActive = script.IsActive;
        existing.LastUpdatedAt = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.AutomationScriptDefinitions
            .Where(x => x.Id == id)
            .ExecuteDeleteAsync();
    }
}
