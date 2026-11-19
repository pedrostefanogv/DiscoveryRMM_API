using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Discovery.Api.Services;

/// <summary>
/// Armazena tickets de uso único para /tunnel/ws no Redis (multi-réplica) com
/// fallback para IMemoryCache em nó único / ambiente de testes sem Redis.
/// </summary>
public sealed class TunnelTicketService : ITunnelTicketService
{
    public const int DefaultTtlSeconds = 60;
    private const string KeyPrefix = "tunnel:ticket:";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRedisService? _redisService;
    private readonly IMemoryCache _memoryCache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TunnelTicketService> _logger;
    private readonly Lock _memoryLock = new();

    public TunnelTicketService(
        IMemoryCache memoryCache,
        ILogger<TunnelTicketService> logger,
        IRedisService? redisService = null,
        TimeProvider? timeProvider = null)
    {
        _memoryCache = memoryCache;
        _logger = logger;
        _redisService = redisService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TunnelTicketIssueResult> IssueAsync(
        Guid userId,
        Guid agentId,
        Guid sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (userId == Guid.Empty)
            throw new ArgumentException("UserId must not be empty.", nameof(userId));
        if (agentId == Guid.Empty)
            throw new ArgumentException("AgentId must not be empty.", nameof(agentId));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("SessionId must not be empty.", nameof(sessionId));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = now.AddSeconds(DefaultTtlSeconds);
        var ticket = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var key = BuildStorageKey(ticket);
        var principal = new TunnelTicketPrincipal(userId, agentId, sessionId, expiresAtUtc);

        if (_redisService is { IsConnected: true })
        {
            var payload = JsonSerializer.Serialize(principal, JsonOptions);
            await _redisService.SetAsync(key, payload, DefaultTtlSeconds);
        }
        else
        {
            lock (_memoryLock)
            {
                _memoryCache.Set(key, principal, TimeSpan.FromSeconds(DefaultTtlSeconds));
            }
        }

        return new TunnelTicketIssueResult(ticket, DefaultTtlSeconds, expiresAtUtc);
    }

    public async Task<TunnelTicketPrincipal?> ConsumeAsync(
        string ticket,
        Guid agentId,
        Guid sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(ticket) || agentId == Guid.Empty || sessionId == Guid.Empty)
            return null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var key = BuildStorageKey(ticket.Trim());

        if (_redisService is { IsConnected: true })
        {
            var raw = await _redisService.GetAsync(key);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            TunnelTicketPrincipal? principal;
            try
            {
                principal = JsonSerializer.Deserialize<TunnelTicketPrincipal>(raw, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "TunnelTicketService: payload inválido no Redis para ticket.");
                return null;
            }

            if (!IsValidMatch(principal, agentId, sessionId, now))
                return null;

            // Consumo atômico: apenas o primeiro caller vence o SET NX.
            var claimed = await _redisService.SetIfNotExistsAsync($"{key}:used", "1", DefaultTtlSeconds + 5);
            if (!claimed)
                return null;

            await _redisService.DeleteAsync(key);
            return principal;
        }

        lock (_memoryLock)
        {
            if (!_memoryCache.TryGetValue<TunnelTicketPrincipal>(key, out var principal))
                return null;

            if (!IsValidMatch(principal, agentId, sessionId, now))
            {
                if (principal is not null && principal.ExpiresAtUtc <= now)
                    _memoryCache.Remove(key);
                return null;
            }

            _memoryCache.Remove(key);
            return principal;
        }
    }

    private static bool IsValidMatch(
        TunnelTicketPrincipal? principal,
        Guid agentId,
        Guid sessionId,
        DateTime nowUtc)
    {
        return principal is not null
            && principal.UserId != Guid.Empty
            && principal.AgentId == agentId
            && principal.SessionId == sessionId
            && principal.ExpiresAtUtc > nowUtc;
    }

    private static string BuildStorageKey(string ticket)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ticket));
        return KeyPrefix + Convert.ToHexStringLower(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
