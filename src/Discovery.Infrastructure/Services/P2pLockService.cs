using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Lock global via Redis para download de artifacts P2P.
/// Usa SETNX-style: GET + SET com double-check para atomicidade aproximada.
/// </summary>
public class P2pLockService : IP2pLockService
{
    private readonly IRedisService _redis;
    private readonly ILogger<P2pLockService> _logger;

    private const string KeyPrefix = "p2p:lock:";

    public P2pLockService(IRedisService redis, ILogger<P2pLockService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<(bool Acquired, string? HolderToken)> TryAcquireAsync(
        Guid clientId, Guid artifactId, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogDebug("Redis not connected — allowing lock acquisition as fallback");
            return (true, Guid.NewGuid().ToString("N"));
        }

        ct.ThrowIfCancellationRequested();

        var key = BuildKey(clientId, artifactId);
        var holderToken = Guid.NewGuid().ToString("N");
        var expiry = (int)(ttl ?? TimeSpan.FromMinutes(5)).TotalSeconds;

        try
        {
            // GET para verificar se já existe
            var existing = await _redis.GetAsync(key);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                _logger.LogDebug("Lock already held for {Key} by {Holder}", key, existing);
                return (false, null);
            }

            // Race condition window, mas aceitável para este uso
            await _redis.SetAsync(key, holderToken, expiry);

            // Double-check
            var owner = await _redis.GetAsync(key);
            var acquired = owner == holderToken;

            if (acquired)
                _logger.LogDebug("Lock acquired for {Key} (token: {Token})", key, holderToken);

            return (acquired, acquired ? holderToken : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error acquiring lock for {Key}", key);
            return (false, null);
        }
    }

    public async Task ReleaseAsync(Guid clientId, Guid artifactId, CancellationToken ct = default)
    {
        if (!_redis.IsConnected) return;

        ct.ThrowIfCancellationRequested();

        var key = BuildKey(clientId, artifactId);
        await _redis.DeleteAsync(key);
        _logger.LogDebug("Lock released for {Key}", key);
    }

    public async Task<bool> ExistsAsync(Guid clientId, Guid artifactId, CancellationToken ct = default)
    {
        if (!_redis.IsConnected) return false;

        ct.ThrowIfCancellationRequested();

        var key = BuildKey(clientId, artifactId);
        var value = await _redis.GetAsync(key);
        return !string.IsNullOrWhiteSpace(value);
    }

    public async Task<bool> RenewAsync(Guid clientId, Guid artifactId, string holderToken, CancellationToken ct = default)
    {
        if (!_redis.IsConnected) return false;

        ct.ThrowIfCancellationRequested();

        var key = BuildKey(clientId, artifactId);
        var current = await _redis.GetAsync(key);

        if (current != holderToken)
        {
            _logger.LogDebug("Cannot renew lock for {Key}: current holder {Current} != expected {Expected}",
                key, current, holderToken);
            return false;
        }

        await _redis.SetExpiryAsync(key, (int)TimeSpan.FromMinutes(5).TotalSeconds);
        _logger.LogDebug("Lock renewed for {Key}", key);
        return true;
    }

    private static string BuildKey(Guid clientId, Guid artifactId)
        => $"{KeyPrefix}{clientId:N}:{artifactId:N}";
}
