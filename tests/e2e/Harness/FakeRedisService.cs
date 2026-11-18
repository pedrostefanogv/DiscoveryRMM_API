using System.Collections.Concurrent;
using System.Reflection;
using Discovery.Core.Interfaces;
using StackExchange.Redis;

namespace Discovery.E2E.Tests.Harness;

/// <summary>
/// Thread-safe in-memory IRedisService supporting TTL, NX locks (SetIfNotExistsAsync),
/// counters, prefix queries/deletes, and pub/sub without any Redis server.
/// </summary>
public sealed class FakeRedisService : IRedisService
{
    private sealed record Entry(string Value, DateTime? ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Action<string, string>>> _subscribers = new(StringComparer.Ordinal);

    public bool IsConnected => true;

    public Task<int?> PingAsync(CancellationToken ct = default) => Task.FromResult<int?>(1);

    public Task<string?> GetAsync(string key)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc is { } exp && exp <= DateTime.UtcNow)
            {
                _store.TryRemove(key, out _);
                return Task.FromResult<string?>(null);
            }
            return Task.FromResult<string?>(entry.Value);
        }

        return Task.FromResult<string?>(null);
    }

    public Task<long> IncrementAsync(string key) => IncrementByAsync(key, 1);

    public Task<long> IncrementByAsync(string key, long amount)
    {
        while (true)
        {
            if (_store.TryGetValue(key, out var current))
            {
                if (current.ExpiresAtUtc is { } exp && exp <= DateTime.UtcNow)
                {
                    if (_store.TryRemove(new KeyValuePair<string, Entry>(key, current)))
                        continue;
                }

                var parsed = long.TryParse(current.Value, out var val) ? val : 0L;
                var next = parsed + amount;
                if (_store.TryUpdate(key, current with { Value = next.ToString() }, current))
                    return Task.FromResult(next);
            }
            else
            {
                var fresh = new Entry(amount.ToString(), null);
                if (_store.TryAdd(key, fresh))
                    return Task.FromResult(amount);
            }
        }
    }

    public Task SetAsync(string key, string value, int expirySeconds = 3600)
    {
        DateTime? exp = expirySeconds > 0 ? DateTime.UtcNow.AddSeconds(expirySeconds) : null;
        _store[key] = new Entry(value, exp);
        return Task.CompletedTask;
    }

    public Task<bool> SetExpiryAsync(string key, int expirySeconds)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc is { } exp && exp <= DateTime.UtcNow)
            {
                _store.TryRemove(key, out _);
                return Task.FromResult(false);
            }

            _store[key] = entry with { ExpiresAtUtc = expirySeconds > 0 ? DateTime.UtcNow.AddSeconds(expirySeconds) : null };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<int> GetTtlSecondsAsync(string key)
    {
        if (!_store.TryGetValue(key, out var entry))
            return Task.FromResult(-2);

        if (entry.ExpiresAtUtc is null)
            return Task.FromResult(-1);

        var remaining = (int)Math.Ceiling((entry.ExpiresAtUtc.Value - DateTime.UtcNow).TotalSeconds);
        if (remaining <= 0)
        {
            _store.TryRemove(key, out _);
            return Task.FromResult(-2);
        }

        return Task.FromResult(remaining);
    }

    public Task DeleteAsync(string key)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task DeleteByPrefixAsync(string prefix)
    {
        foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task PublishAsync(string channel, string message)
    {
        if (_subscribers.TryGetValue(channel, out var handlers))
        {
            Action<string, string>[] snapshot;
            lock (handlers)
                snapshot = handlers.ToArray();

            foreach (var h in snapshot)
                h(channel, message);
        }
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string channel, Action<string, string> handler)
    {
        var handlers = _subscribers.GetOrAdd(channel, _ => new List<Action<string, string>>());
        lock (handlers)
            handlers.Add(handler);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
    {
        var now = DateTime.UtcNow;
        var keys = new List<string>();
        foreach (var kv in _store)
        {
            if (kv.Value.ExpiresAtUtc is { } exp && exp <= now)
            {
                _store.TryRemove(kv.Key, out _);
                continue;
            }
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                keys.Add(kv.Key);
                if (keys.Count >= maxResults)
                    break;
            }
        }
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds)
    {
        var now = DateTime.UtcNow;
        DateTime? exp = expirySeconds > 0 ? now.AddSeconds(expirySeconds) : null;
        var fresh = new Entry(value, exp);

        while (true)
        {
            if (_store.TryAdd(key, fresh))
                return Task.FromResult(true);

            if (_store.TryGetValue(key, out var existing) && existing.ExpiresAtUtc is { } existingExp && existingExp <= now)
            {
                if (_store.TryUpdate(key, fresh, existing))
                    return Task.FromResult(true);
                continue;
            }

            return Task.FromResult(false);
        }
    }

    public void Clear() => _store.Clear();
}

/// <summary>
/// Minimal DispatchProxy for IConnectionMultiplexer so DI never opens a real Redis TCP socket.
/// </summary>
public class FakeConnectionMultiplexerProxy : DispatchProxy
{
    public static IConnectionMultiplexer Create() => Create<IConnectionMultiplexer, FakeConnectionMultiplexerProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;

        if (targetMethod.Name == "get_IsConnected")
            return true;

        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(void))
            return null;
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (returnType == typeof(Task<bool>))
            return Task.FromResult(true);
        if (returnType == typeof(Task<TimeSpan>))
            return Task.FromResult(TimeSpan.FromMilliseconds(1));

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
