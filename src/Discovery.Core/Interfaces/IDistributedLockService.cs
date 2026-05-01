namespace Discovery.Core.Interfaces;

/// <summary>
/// Distributed lock service for coordinating operations across multiple API instances.
/// Uses Redis SETNX with TTL under the hood.
/// </summary>
public interface IDistributedLockService
{
    /// <summary>
    /// Attempts to acquire a distributed lock. Returns null if lock is already held.
    /// The lock is automatically released on Dispose() or when the TTL expires.
    /// </summary>
    /// <param name="key">Lock key (namespaced, e.g. "dedup:monitoring:{fingerprint}")</param>
    /// <param name="expiry">Maximum lock duration</param>
    /// <returns>A disposable handle if acquired, null otherwise</returns>
    Task<IDisposable?> AcquireAsync(string key, TimeSpan expiry);
}
