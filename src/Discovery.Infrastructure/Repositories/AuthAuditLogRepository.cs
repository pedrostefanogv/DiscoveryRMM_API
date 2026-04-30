using Discovery.Core.Entities.Security;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AuthAuditLogRepository : IAuthAuditLogRepository
{
    private readonly DiscoveryDbContext _db;

    public AuthAuditLogRepository(DiscoveryDbContext db) => _db = db;

    public async Task AddAsync(AuthAuditLog log)
    {
        _db.AuthAuditLogs.Add(log);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<AuthAuditLog>> GetByUserAsync(Guid userId, int limit = 100)
    {
        return await _db.AuthAuditLogs
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.OccurredAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<IEnumerable<AuthAuditLog>> GetRecentAsync(int limit = 200)
    {
        return await _db.AuthAuditLogs
            .OrderByDescending(l => l.OccurredAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<IEnumerable<AuthAuditLog>> GetFailedAsync(DateTime since, int limit = 100)
    {
        return await _db.AuthAuditLogs
            .Where(l => !l.Success && l.OccurredAt >= since)
            .OrderByDescending(l => l.OccurredAt)
            .Take(limit)
            .ToListAsync();
    }
}
