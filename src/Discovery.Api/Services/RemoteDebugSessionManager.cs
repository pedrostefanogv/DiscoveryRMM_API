using System.Collections.Concurrent;
using Discovery.Core.Helpers;

namespace Discovery.Api.Services;

public sealed class RemoteDebugSessionManager : IRemoteDebugSessionManager
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<Guid, RemoteDebugSessionState> _sessions = new();

    public RemoteDebugSessionState StartSession(
        Guid agentId,
        Guid userId,
        Guid clientId,
        Guid siteId,
        string? logLevel,
        int? ttlMinutes,
        string? preferredTransport = null)
    {
        var now = DateTime.UtcNow;
        var ttl = ResolveTtl(ttlMinutes);

        // Mantem apenas uma sessao ativa por agent para evitar colisao de streams.
        foreach (var pair in _sessions)
        {
            if (pair.Value.AgentId == agentId && !pair.Value.IsClosed)
            {
                CloseSession(pair.Key, "superseded");
            }
        }

        var sessionId = Guid.NewGuid();
        var normalizedLevel = NormalizeLogLevel(logLevel);
        var normalizedTransport = NormalizePreferredTransport(preferredTransport);

        var state = new RemoteDebugSessionState
        {
            SessionId = sessionId,
            AgentId = agentId,
            OwnerUserId = userId,
            ClientId = clientId,
            SiteId = siteId,
            LogLevel = normalizedLevel,
            StartedAtUtc = now,
            LastActivityAtUtc = now,
            ExpiresAtUtc = now.Add(ttl),
            PreferredTransport = normalizedTransport,
            FallbackTransport = normalizedTransport == RemoteDebugTransportNames.Nats
                ? RemoteDebugTransportNames.SignalR
                : RemoteDebugTransportNames.Nats,
            NatsSubject = NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "remote-debug.log")
        };

        _sessions[sessionId] = state;
        return state;
    }

    public bool TryGetSessionForUser(Guid sessionId, Guid userId, out RemoteDebugSessionState? session)
    {
        session = null;
        if (!TryGetSession(sessionId, out var found))
            return false;

        if (found!.OwnerUserId != userId)
            return false;

        session = found;
        return true;
    }

    public bool TryGetSessionForAgent(Guid sessionId, Guid agentId, out RemoteDebugSessionState? session)
    {
        session = null;
        if (!TryGetSession(sessionId, out var found))
            return false;

        if (found!.AgentId != agentId)
            return false;

        session = found;
        return true;
    }

    public bool TryGetSession(Guid sessionId, out RemoteDebugSessionState? session)
    {
        session = null;
        if (!_sessions.TryGetValue(sessionId, out var found))
            return false;

        if (found.IsClosed)
            return false;

        if (found.ExpiresAtUtc <= DateTime.UtcNow)
        {
            CloseSession(sessionId, "timeout");
            return false;
        }

        session = found;
        return true;
    }

    public bool CloseSession(Guid sessionId, string reason, Guid? closedByUserId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var found))
            return false;

        if (found.IsClosed)
            return true;

        found.EndedAtUtc = DateTime.UtcNow;
        found.EndReason = reason;
        found.ClosedByUserId = closedByUserId;
        return true;
    }

    public void Touch(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var found) || found.IsClosed)
            return;

        var now = DateTime.UtcNow;
        found.LastActivityAtUtc = now;
        if (found.ExpiresAtUtc < now.AddMinutes(2))
            found.ExpiresAtUtc = now.AddMinutes(2);
    }

    public long NextSequence(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var found))
            return 0;

        return Interlocked.Increment(ref found.SequenceCounter);
    }

    public int CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        var cleaned = 0;

        foreach (var pair in _sessions)
        {
            var session = pair.Value;
            if (session.IsClosed)
            {
                if (session.EndedAtUtc.HasValue && session.EndedAtUtc.Value <= now.AddMinutes(-10))
                {
                    _sessions.TryRemove(pair.Key, out _);
                    cleaned++;
                }

                continue;
            }

            if (session.ExpiresAtUtc <= now)
            {
                CloseSession(pair.Key, "timeout");
                cleaned++;
            }
        }

        return cleaned;
    }

    private static TimeSpan ResolveTtl(int? ttlMinutes)
    {
        if (!ttlMinutes.HasValue)
            return DefaultTtl;

        var ttl = TimeSpan.FromMinutes(ttlMinutes.Value);
        if (ttl < MinTtl)
            return MinTtl;
        if (ttl > MaxTtl)
            return MaxTtl;

        return ttl;
    }

    private static string NormalizeLogLevel(string? logLevel)
    {
        if (string.IsNullOrWhiteSpace(logLevel))
            return "info";

        var normalized = logLevel.Trim().ToLowerInvariant();
        return normalized is "trace" or "debug" or "info" or "warn" or "error"
            ? normalized
            : "info";
    }

    private static string NormalizePreferredTransport(string? preferredTransport)
    {
        if (string.IsNullOrWhiteSpace(preferredTransport))
            return RemoteDebugTransportNames.Nats;

        var normalized = preferredTransport.Trim().ToLowerInvariant();
        return normalized is RemoteDebugTransportNames.Nats or RemoteDebugTransportNames.SignalR
            ? normalized
            : RemoteDebugTransportNames.Nats;
    }
}
