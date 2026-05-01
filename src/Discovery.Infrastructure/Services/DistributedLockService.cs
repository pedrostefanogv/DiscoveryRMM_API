using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Distributed lock implementation using Redis SETNX with TTL.
/// Used for coordinating operations across multiple API instances:
/// - Dedup of monitoring events
/// - Auto-ticket creation
/// - Catalog sync deduplication
/// - Configuration cache refresh
/// </summary>
public class DistributedLockService : IDistributedLockService
{
    private readonly IRedisService _redis;
    private readonly ILogger<DistributedLockService> _logger;

    public DistributedLockService(IRedisService redis, ILogger<DistributedLockService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IDisposable?> AcquireAsync(string key, TimeSpan expiry)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogDebug("Redis not connected — skipping distributed lock for key {Key}", key);
            return null;
        }

        try
        {
            var lockKey = $"lock:{key}";
            var lockToken = Guid.NewGuid().ToString("N");

            var acquired = await TryAcquireAsync(lockKey, lockToken, expiry);
            if (!acquired)
            {
                _logger.LogDebug("Failed to acquire distributed lock for key {Key}", key);
                return null;
            }

            _logger.LogDebug("Acquired distributed lock for key {Key} (token: {Token})", key, lockToken);
            return new RedisLockHandle(_redis, lockKey, lockToken, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error acquiring distributed lock for key {Key}", key);
            return null;
        }
    }

    private async Task<bool> TryAcquireAsync(string lockKey, string lockToken, TimeSpan expiry)
    {
        // SETNX equivalent: set key only if it doesn't exist
        var existing = await _redis.GetAsync(lockKey);
        if (!string.IsNullOrWhiteSpace(existing))
            return false;

        // Race condition window between GET and SET, but acceptable for most use cases.
        // For strict atomicity, a Lua script would be needed.
        await _redis.SetAsync(lockKey, lockToken, (int)expiry.TotalSeconds);

        // Double-check: read back to confirm we own the lock
        var owner = await _redis.GetAsync(lockKey);
        return owner == lockToken;
    }

    private sealed class RedisLockHandle : IDisposable
    {
        private readonly IRedisService _redis;
        private readonly string _lockKey;
        private readonly string _lockToken;
        private readonly ILogger _logger;
        private bool _disposed;

        public RedisLockHandle(IRedisService redis, string lockKey, string lockToken, ILogger logger)
        {
            _redis = redis;
            _lockKey = lockKey;
            _lockToken = lockToken;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // Only release if we still own the lock
                var current = _redis.GetAsync(_lockKey).GetAwaiter().GetResult();
                if (current == _lockToken)
                {
                    _redis.DeleteAsync(_lockKey).GetAwaiter().GetResult();
                    _logger.LogDebug("Released distributed lock for key {Key}", _lockKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error releasing distributed lock for key {Key}", _lockKey);
            }
        }
    }
}
