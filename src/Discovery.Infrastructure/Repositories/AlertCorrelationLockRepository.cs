using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AlertCorrelationLockRepository : IAlertCorrelationLockRepository
{
    private readonly DiscoveryDbContext _db;

    public AlertCorrelationLockRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AlertCorrelationLock?> GetByDedupKeyAsync(string dedupKey)
    {
        return await _db.AlertCorrelationLocks
            .AsNoTracking()
            .SingleOrDefaultAsync(correlationLock => correlationLock.DedupKey == dedupKey);
    }

    public async Task<AlertCorrelationLock> CreateAsync(AlertCorrelationLock correlationLock)
    {
        _db.AlertCorrelationLocks.Add(correlationLock);
        await _db.SaveChangesAsync();
        return correlationLock;
    }

    public async Task<AlertCorrelationLock> UpdateAsync(AlertCorrelationLock correlationLock)
    {
        _db.AlertCorrelationLocks.Update(correlationLock);
        await _db.SaveChangesAsync();
        return correlationLock;
    }
}