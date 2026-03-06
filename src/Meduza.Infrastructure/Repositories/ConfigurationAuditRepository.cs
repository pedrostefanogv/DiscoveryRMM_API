using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class ConfigurationAuditRepository : IConfigurationAuditRepository
{
    private readonly MeduzaDbContext _db;

    public ConfigurationAuditRepository(MeduzaDbContext db) => _db = db;

    public async Task CreateAsync(ConfigurationAudit audit)
    {
        audit.Id = IdGenerator.NewId();
        audit.ChangedAt = DateTime.UtcNow;
        _db.ConfigurationAudits.Add(audit);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetByEntityAsync(string entityType, Guid entityId, int limit = 100)
    {
        if (!Enum.TryParse<ConfigurationEntityType>(entityType, true, out var parsedType))
            return [];

        var safeLimit = Math.Clamp(limit, 1, 5000);

        return await _db.ConfigurationAudits
            .AsNoTracking()
            .Where(audit => audit.EntityType == parsedType && audit.EntityId == entityId)
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetRecentAsync(int days = 90, int limit = 1000)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var safeLimit = Math.Clamp(limit, 1, 5000);

        return await _db.ConfigurationAudits
            .AsNoTracking()
            .Where(audit => audit.ChangedAt >= since)
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetByUserAsync(string username, int limit = 100)
    {
        var safeLimit = Math.Clamp(limit, 1, 5000);

        return await _db.ConfigurationAudits
            .AsNoTracking()
            .Where(audit => audit.ChangedBy == username)
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }
}
