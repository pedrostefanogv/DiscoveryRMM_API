using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AutomationScriptRepository : IAutomationScriptRepository
{
    private readonly DiscoveryDbContext _db;

    public AutomationScriptRepository(DiscoveryDbContext db) => _db = db;

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
        var query = _db.AutomationScriptDefinitions
            .AsNoTracking()
            .Where(script => script.Id == id && script.DeletedAt == null);
        if (!includeInactive)
            query = query.Where(script => script.IsActive);

        return await query.SingleOrDefaultAsync();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var idList = ids.Distinct().ToList();
        var rows = await _db.AutomationScriptDefinitions
            .AsNoTracking()
            .Where(script => idList.Contains(script.Id))
            .Select(script => new { script.Id, script.Name })
            .ToListAsync();

        return rows.ToDictionary(row => row.Id, row => row.Name);
    }

    public async Task<IReadOnlyDictionary<Guid, AutomationScriptDefinition>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        bool includeInactive = false)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, AutomationScriptDefinition>();

        var idList = ids.Distinct().ToList();
        var query = _db.AutomationScriptDefinitions
            .AsNoTracking()
            .Where(script => idList.Contains(script.Id) && script.DeletedAt == null);
        if (!includeInactive)
            query = query.Where(script => script.IsActive);

        var rows = await query.ToListAsync();
        return rows.ToDictionary(script => script.Id);
    }

    public async Task<IReadOnlyList<AutomationScriptDefinition>> GetListPageAsync(
        Guid? clientId,
        bool activeOnly,
        string? cursor,
        int limit,
        string? search = null,
        AutomationScriptType? scriptType = null)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var query = BuildScriptQuery(clientId, activeOnly, search, scriptType);

        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorUpdatedAtUtc, out var cursorId))
        {
            query = CursorPaginationHelper.ApplyCreatedAtCursor(
                query, cursorUpdatedAtUtc, cursorId,
                script => script.UpdatedAt, script => script.Id);
        }

        return await query
            .OrderByDescending(script => script.UpdatedAt)
            .ThenByDescending(script => script.Id)
            .Take(safeLimit + 1)
            .ToListAsync();
    }

    private IQueryable<AutomationScriptDefinition> BuildScriptQuery(
        Guid? clientId,
        bool activeOnly,
        string? search = null,
        AutomationScriptType? scriptType = null)
    {
        var query = _db.AutomationScriptDefinitions
            .AsNoTracking()
            .Where(script => script.DeletedAt == null);

        if (clientId.HasValue)
            query = query.Where(script => script.ClientId == clientId.Value || script.ClientId == null);

        if (activeOnly)
            query = query.Where(script => script.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(script =>
                EF.Functions.ILike(script.Name, pattern)
                || EF.Functions.ILike(script.Summary, pattern));
        }

        if (scriptType.HasValue)
            query = query.Where(script => script.ScriptType == scriptType.Value);

        return query;
    }

    public async Task<int> CountAsync(
        Guid? clientId,
        bool activeOnly,
        string? search = null,
        AutomationScriptType? scriptType = null)
    {
        return await BuildScriptQuery(clientId, activeOnly, search, scriptType).CountAsync();
    }

    public async Task UpdateAsync(AutomationScriptDefinition script)
    {
        var existing = await _db.AutomationScriptDefinitions.SingleOrDefaultAsync(x => x.Id == script.Id && x.DeletedAt == null);
        if (existing is null)
            return;

        // Whitelist deliberada: só o que é copiado é persistido (GetByIdAsync usa
        // AsNoTracking, então a entidade chega DESTACADA). ClientId NÃO é copiado
        // de propósito — o contrato de update de script não expõe mudança de
        // cliente. Se isso mudar, inclua o campo aqui: a omissão é silenciosa (foi
        // assim com NotificationMode nas automation tasks).

        existing.Name = script.Name;
        existing.Summary = script.Summary;
        existing.ScriptType = script.ScriptType;
        existing.Version = script.Version;
        existing.Content = script.Content;
        existing.IsActive = script.IsActive;
        existing.LastUpdatedAt = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var existing = await _db.AutomationScriptDefinitions.SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null);
        if (existing is null)
            return;

        // Soft-delete preserva a FK de automation_script_audits (e a hash chain M241)
        // além de evitar que o INSERT do evento Deleted exploda com 23503.
        var now = DateTime.UtcNow;
        existing.IsActive = false;
        existing.DeletedAt = now;
        existing.LastUpdatedAt = now;
        existing.UpdatedAt = now;

        await _db.SaveChangesAsync();
    }
}
