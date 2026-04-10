using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AutomationTaskAuditRepository : IAutomationTaskAuditRepository
{
    private readonly DiscoveryDbContext _db;

    public AutomationTaskAuditRepository(DiscoveryDbContext db) => _db = db;

    public async Task CreateAsync(AutomationTaskAudit audit)
    {
        audit.Id = IdGenerator.NewId();
        audit.ChangedAt = DateTime.UtcNow;
        _db.AutomationTaskAudits.Add(audit);
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AutomationTaskAudit>> GetByTaskIdAsync(Guid taskId, int limit = 100)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await _db.AutomationTaskAudits
            .AsNoTracking()
            .Where(x => x.TaskId == taskId)
            .OrderByDescending(x => x.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }
}
