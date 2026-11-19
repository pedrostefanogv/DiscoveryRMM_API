using System.Collections.Concurrent;
using System.Reflection;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using StackExchange.Redis;

namespace Discovery.IntegrationTests.Fixtures;

/// <summary>
/// Thread-safe in-memory IRedisService used only to decouple Redis from PostgreSQL-focused
/// integration tests without mocking any database or EF Core / FluentMigrator layer.
/// </summary>
public sealed class InMemoryRedisService : IRedisService
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
}

public class NullConnectionMultiplexerProxy : DispatchProxy
{
    public static IConnectionMultiplexer Create() => Create<IConnectionMultiplexer, NullConnectionMultiplexerProxy>();

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

public sealed class NoOpHeartbeatCacheService : IHeartbeatCacheService
{
    private readonly ConcurrentDictionary<Guid, HeartbeatCacheEntry> _entries = new();
    private readonly ConcurrentDictionary<Guid, string> _transports = new();

    public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
    {
        var existed = _entries.ContainsKey(heartbeat.AgentId);
        _entries[heartbeat.AgentId] = new HeartbeatCacheEntry
        {
            AgentId = heartbeat.AgentId,
            Status = status,
            IpAddress = heartbeat.IpAddress,
            Hostname = heartbeat.Hostname,
            AgentVersion = heartbeat.AgentVersion,
            LastHeartbeatAt = heartbeat.TimestampUtc is { } ts && ts != default ? ts : DateTime.UtcNow
        };
        return Task.FromResult(!existed);
    }

    public Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
    {
        _entries.TryGetValue(agentId, out var entry);
        return Task.FromResult<HeartbeatCacheEntry?>(entry);
    }

    public Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HeartbeatCacheEntry>>(_entries.Values.ToArray());

    public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default)
    {
        _entries.TryRemove(agentId, out _);
        return Task.CompletedTask;
    }

    public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default)
        => Task.FromResult(_entries.ContainsKey(agentId));

    public Task<IReadOnlyList<Guid>> DetectExpiredAsync(IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>(knownOnlineAgentIds.Where(id => !_entries.ContainsKey(id)).ToArray());

    public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default)
    {
        _transports[agentId] = transport;
        return Task.CompletedTask;
    }

    public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default)
    {
        _transports.TryGetValue(agentId, out var transport);
        return Task.FromResult<string?>(transport);
    }
}

public sealed class NoOpAgentMessaging : IAgentMessaging
{
    public bool IsConnected => true;

    public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
    public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
    public Task PublishRemoteDebugControlAsync(Guid clientId, Guid siteId, Guid agentId, string payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishSupportChatServerAsync(Guid clientId, Guid siteId, Guid agentId, TicketChatEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NoOpNatsTenantStreamProvisioner : INatsTenantStreamProvisioner
{
    public Task EnsureGlobalFanoutStreamAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureTenantFanoutStreamAsync(Guid clientId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureTenantSupportChatStreamAsync(Guid clientId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MigrateLegacyWildcardStreamsAsync(CancellationToken ct = default) => Task.CompletedTask;
}
