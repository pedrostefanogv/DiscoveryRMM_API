using System.Collections.Concurrent;
using Discovery.Core.Configuration;
using Discovery.Core.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Services;

public sealed class RemoteDebugSessionManager : IRemoteDebugSessionManager
{
    private readonly RemoteDebugOptions _options;
    private readonly ILogger<RemoteDebugSessionManager> _logger;
    private readonly ConcurrentDictionary<Guid, RemoteDebugSessionState> _sessions = new();

    public RemoteDebugSessionManager(
        IOptions<RemoteDebugOptions> options,
        ILogger<RemoteDebugSessionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // O teto do debug remoto PRECISA ser positivo: o agente recebe o teto como
    // maxExpiresAtUtc e, sem valor, nao estende a sessao. "0 = ilimitado" aqui
    // criaria uma sessao que o servidor renovaria para sempre e o agente
    // encerraria no deadline inicial. 0 cai no padrao de 1h (instalador).
    private int ResolvedMaxDurationMinutes
        => _options.MaxSessionDurationMinutes > 0 ? _options.MaxSessionDurationMinutes : 60;

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
            LastKeepAliveAtUtc = now,
            ExpiresAtUtc = now.Add(ttl),
            MaxExpiresAtUtc = SessionLivenessPolicy.ResolveMaxExpiresAt(now, ResolvedMaxDurationMinutes)
                             ?? DateTime.MaxValue,
            PreferredTransport = normalizedTransport,
            NatsSubject = NatsSubjectBuilder.RemoteDebugLogSubject(clientId, siteId, agentId),
            NatsControlSubject = NatsSubjectBuilder.RemoteDebugControlSubject(clientId, siteId, agentId)
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

        var now = DateTime.UtcNow;
        if (found.IsClosed)
            return false;

        if (found.ExpiresAtUtc <= now)
        {
            CloseSession(sessionId, "timeout");
            return false;
        }

        // Viewer parou de renovar: a sessao nao pode ficar presa.
        var keepAliveTimeout = _options.KeepAliveTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_options.KeepAliveTimeoutSeconds)
            : TimeSpan.FromSeconds(90);
        if (now - found.LastKeepAliveAtUtc > keepAliveTimeout)
        {
            CloseSession(sessionId, "keepalive-timeout");
            return false;
        }

        if (found.MaxExpiresAtUtc != DateTime.MaxValue && now >= found.MaxExpiresAtUtc)
        {
            CloseSession(sessionId, "max-duration");
            return false;
        }

        session = found;
        return true;
    }

    public bool TryRenewSession(Guid sessionId, Guid userId, out RemoteDebugSessionState? session)
    {
        session = null;
        if (!_sessions.TryGetValue(sessionId, out var found))
            return false;

        // Sessao ja encerrada: devolve o estado (sem vazar de outro usuario) para
        // o caller responder 200 com SessionActive=false — assim o viewer
        // encerra de imediato em vez de renovar em loop silencioso.
        if (found.IsClosed)
        {
            if (found.OwnerUserId == userId)
                session = found;
            return false;
        }

        if (found.OwnerUserId != userId)
            return false;

        var now = DateTime.UtcNow;
        if (!SessionLivenessPolicy.TryClampRenewal(
                now,
                found.StartedAtUtc,
                _options.DefaultTtlMinutes,
                ResolvedMaxDurationMinutes,
                out var nextExpiry))
        {
            // Teto total atingido: encerra e devolve o estado final.
            CloseSession(sessionId, "max-duration", userId);
            session = found;
            return false;
        }

        found.ExpiresAtUtc = nextExpiry;
        found.LastActivityAtUtc = now;
        found.LastKeepAliveAtUtc = now;
        session = found;
        _logger.LogInformation(
            "[remote-debug] sessao renovada: sessionId={SessionId} expiresAtUtc={ExpiresAtUtc} maxExpiresAtUtc={MaxExpiresAtUtc}",
            found.SessionId, found.ExpiresAtUtc, found.MaxExpiresAtUtc);
        return true;
    }

    public bool TrySetLogLevel(Guid sessionId, Guid userId, string? logLevel, out RemoteDebugSessionState? session)
    {
        session = null;
        if (!TryGetSessionForUser(sessionId, userId, out var found) || found is null)
            return false;

        found.LogLevel = NormalizeLogLevel(logLevel);
        found.LastActivityAtUtc = DateTime.UtcNow;
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
        _logger.LogInformation(
            "[remote-debug] sessao encerrada: sessionId={SessionId} reason={Reason}",
            found.SessionId, reason);
        return true;
    }

    public void Touch(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var found) || found.IsClosed)
            return;

        found.LastActivityAtUtc = DateTime.UtcNow;
        found.LastKeepAliveAtUtc = DateTime.UtcNow;

        // Mantem o comportamento antigo de estender quando perto de expirar,
        // agora respeitando o teto total.
        if (found.ExpiresAtUtc < DateTime.UtcNow.AddMinutes(_options.MinTtlMinutes))
        {
            if (SessionLivenessPolicy.TryClampRenewal(
                    DateTime.UtcNow,
                    found.StartedAtUtc,
                    _options.DefaultTtlMinutes,
                    _options.MaxSessionDurationMinutes,
                    out var nextExpiry))
            {
                found.ExpiresAtUtc = nextExpiry;
            }
        }
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
        var keepAliveTimeout = _options.KeepAliveTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_options.KeepAliveTimeoutSeconds)
            : TimeSpan.FromSeconds(90);

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
                continue;
            }

            if (now - session.LastKeepAliveAtUtc > keepAliveTimeout)
            {
                CloseSession(pair.Key, "keepalive-timeout");
                cleaned++;
                continue;
            }

            if (session.MaxExpiresAtUtc != DateTime.MaxValue && now >= session.MaxExpiresAtUtc)
            {
                CloseSession(pair.Key, "max-duration");
                cleaned++;
            }
        }

        return cleaned;
    }

    private TimeSpan ResolveTtl(int? ttlMinutes)
    {
        var minutes = ttlMinutes ?? _options.DefaultTtlMinutes;

        var min = _options.MinTtlMinutes > 0 ? _options.MinTtlMinutes : 2;
        var max = _options.MaxTtlMinutes > 0 ? _options.MaxTtlMinutes : 120;

        if (minutes < min) minutes = min;
        if (minutes > max) minutes = max;

        return TimeSpan.FromMinutes(minutes);
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
        return normalized is RemoteDebugTransportNames.Nats or RemoteDebugTransportNames.NatsWs
            ? normalized
            : RemoteDebugTransportNames.Nats;
    }
}
