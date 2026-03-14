using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AutomationScriptAuditRepository : IAutomationScriptAuditRepository
{
    private readonly MeduzaDbContext _db;

    public AutomationScriptAuditRepository(MeduzaDbContext db) => _db = db;

    public async Task CreateAsync(AutomationScriptAudit audit)
    {
        audit.Id = IdGenerator.NewId();
        audit.ChangedAt = DateTime.UtcNow;
        _db.AutomationScriptAudits.Add(audit);
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AutomationScriptAudit>> GetByScriptIdAsync(Guid scriptId, int limit = 100)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await _db.AutomationScriptAudits
            .AsNoTracking()
            .Where(x => x.ScriptId == scriptId)
            .OrderByDescending(x => x.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }
}
