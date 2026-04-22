using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAlertCorrelationLockRepository
{
    Task<AlertCorrelationLock?> GetByDedupKeyAsync(string dedupKey);
    Task<AlertCorrelationLock> CreateAsync(AlertCorrelationLock correlationLock);
    Task<AlertCorrelationLock> UpdateAsync(AlertCorrelationLock correlationLock);
}