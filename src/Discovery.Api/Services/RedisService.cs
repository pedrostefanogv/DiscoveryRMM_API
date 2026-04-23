using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Discovery.Api.Services;

public class RedisService : IRedisService
{
    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisService> _logger;
    private readonly ConcurrentDictionary<string, Action<string, string>> _subscriptions = new();

    public RedisService(IConnectionMultiplexer connection, ILogger<RedisService> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public bool IsConnected => _connection?.IsConnected ?? false;

    public async Task<string?> GetAsync(string key)
    {
        try
        {
            var db = _connection.GetDatabase();
            var value = await db.StringGetAsync(key);
            return value.IsNull ? null : value.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting key {Key} from Redis", LogSanitizer.Sanitize(key));
            return null;
        }
    }

    public async Task<long> IncrementAsync(string key)
    {
        try
        {
            var db = _connection.GetDatabase();
            return await db.StringIncrementAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error incrementing key {Key} in Redis", key);
            return 0;
        }
    }

    public async Task SetAsync(string key, string value, int expirySeconds = 3600)
    {
        try
        {
            var db = _connection.GetDatabase();
            var expiry = TimeSpan.FromSeconds(expirySeconds);
            await db.StringSetAsync(key, value, expiry);
            _logger.LogDebug("Set key {Key} in Redis with {Seconds}s expiry", LogSanitizer.Sanitize(key), expirySeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting key {Key} in Redis", LogSanitizer.Sanitize(key));
        }
    }

    public async Task<bool> SetExpiryAsync(string key, int expirySeconds)
    {
        try
        {
            var db = _connection.GetDatabase();
            return await db.KeyExpireAsync(key, TimeSpan.FromSeconds(expirySeconds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting expiry for key {Key} in Redis", key);
            return false;
        }
    }

    public async Task<int> GetTtlSecondsAsync(string key)
    {
        try
        {
            var db = _connection.GetDatabase();
            var ttl = await db.KeyTimeToLiveAsync(key);
            if (!ttl.HasValue)
                return 0;

            var seconds = (int)Math.Ceiling(ttl.Value.TotalSeconds);
            return Math.Max(0, seconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting TTL for key {Key} in Redis", key);
            return 0;
        }
    }

    public async Task DeleteAsync(string key)
    {
        try
        {
            var db = _connection.GetDatabase();
            await db.KeyDeleteAsync(key);
            _logger.LogDebug("Deleted key {Key} from Redis", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting key {Key} from Redis", key);
        }
    }

    public async Task DeleteByPrefixAsync(string prefix)
    {
        try
        {
            var endpoints = _connection.GetEndPoints();
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints)
            {
                var server = _connection.GetServer(endpoint);
                if (!server.IsConnected)
                    continue;

                var keys = server.Keys(pattern: $"{prefix}*").ToArray();
                if (keys.Length == 0)
                    continue;

                var db = _connection.GetDatabase();
                await db.KeyDeleteAsync(keys);
                _logger.LogDebug("Deleted {Count} Redis keys with prefix {Prefix}", keys.Length, prefix);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting Redis keys by prefix {Prefix}", prefix);
        }
    }

    public async Task PublishAsync(string channel, string message)
    {
        try
        {
            var subscriber = _connection.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(channel), message);
            _logger.LogDebug("Published message to Redis channel {Channel}", LogSanitizer.Sanitize(channel));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing to Redis channel {Channel}", LogSanitizer.Sanitize(channel));
        }
    }

    public async Task SubscribeAsync(string channel, Action<string, string> handler)
    {
        try
        {
            if (!_subscriptions.TryAdd(channel, handler))
            {
                _logger.LogWarning("Already subscribed to channel {Channel}", channel);
                return;
            }

            var subscriber = _connection.GetSubscriber();

            await subscriber.SubscribeAsync(RedisChannel.Literal(channel), (chan, message) =>
            {
                handler(chan.ToString(), message.ToString());
            });

            _logger.LogInformation("Subscribed to Redis channel {Channel}", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing to Redis channel {Channel}", channel);
        }
    }
}
