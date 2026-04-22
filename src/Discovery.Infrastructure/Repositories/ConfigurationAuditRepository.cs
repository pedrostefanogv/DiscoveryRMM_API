using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class ConfigurationAuditRepository : IConfigurationAuditRepository
{
    private readonly DiscoveryDbContext _db;

    public ConfigurationAuditRepository(DiscoveryDbContext db) => _db = db;

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
