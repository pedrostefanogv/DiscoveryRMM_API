using System.Collections.Concurrent;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.E2E.Tests.Harness;

/// <summary>
/// Thread-safe in-memory IHeartbeatCacheService for E2E tests.
/// Tracks transitions (Offline->Online returns true on first SetHeartbeatAsync).
/// </summary>
public sealed class FakeHeartbeatCacheService : IHeartbeatCacheService
{
    private readonly ConcurrentDictionary<Guid, HeartbeatCacheEntry> _entries = new();
    private readonly ConcurrentDictionary<Guid, string> _transports = new();

    public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
    {
        var existed = _entries.ContainsKey(heartbeat.AgentId);
        var entry = new HeartbeatCacheEntry
        {
            AgentId = heartbeat.AgentId,
            Status = status,
            IpAddress = heartbeat.IpAddress,
            Hostname = heartbeat.Hostname,
            AgentVersion = heartbeat.AgentVersion,
            LastHeartbeatAt = heartbeat.TimestampUtc is { } ts && ts != default ? ts : DateTime.UtcNow,
            CpuPercent = heartbeat.CpuPercent,
            CpuTemperatureCelsius = heartbeat.CpuTemperatureCelsius,
            MemoryPercent = heartbeat.MemoryPercent,
            MemoryTotalGb = heartbeat.MemoryTotalGb,
            MemoryUsedGb = heartbeat.MemoryUsedGb,
            DiskPercent = heartbeat.DiskPercent,
            DiskTotalGb = heartbeat.DiskTotalGb,
            DiskUsedGb = heartbeat.DiskUsedGb,
            DiskReadPercent = heartbeat.DiskReadPercent,
            DiskWritePercent = heartbeat.DiskWritePercent,
            DiskResponseMs = heartbeat.DiskResponseMs,
            P2pPeers = heartbeat.P2pPeers,
            UptimeSeconds = heartbeat.UptimeSeconds,
            ProcessCount = heartbeat.ProcessCount,
            PeerId = heartbeat.PeerId,
            Addrs = heartbeat.Addrs,
            Port = heartbeat.Port,
            UiOnline = heartbeat.UiOnline,
            LoggedUser = heartbeat.LoggedUser,
            LoggedUserSince = heartbeat.LoggedUserSince,
            UiOnlineCount = heartbeat.UiOnlineCount
        };
        _entries[heartbeat.AgentId] = entry;
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
    {
        var expired = knownOnlineAgentIds.Where(id => !_entries.ContainsKey(id)).ToArray();
        return Task.FromResult<IReadOnlyList<Guid>>(expired);
    }

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

    public void SeedOnline(Guid agentId, AgentStatus status = AgentStatus.Online)
    {
        _entries[agentId] = new HeartbeatCacheEntry
        {
            AgentId = agentId,
            Status = status,
            LastHeartbeatAt = DateTime.UtcNow
        };
    }

    public void Clear()
    {
        _entries.Clear();
        _transports.Clear();
    }
}
