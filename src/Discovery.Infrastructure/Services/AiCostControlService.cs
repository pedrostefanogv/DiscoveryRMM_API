using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Controla rate limiting e budget de tokens baseado em Redis (fallback: memória local).
/// </summary>
public interface IAiCostControlService
{
    /// <summary>
    /// Verifica se a requisição está dentro do rate limit e do budget diário.
    /// Retorna true se permitido; false se bloqueado.
    /// </summary>
    Task<bool> TryAcquireAsync(
        Guid clientId,
        Guid siteId,
        AIIntegrationSettings settings,
        CancellationToken ct = default);

    /// <summary>
    /// Registra tokens consumidos após a requisição.
    /// </summary>
    Task RecordUsageAsync(
        Guid clientId,
        Guid siteId,
        int tokensUsed,
        CancellationToken ct = default);
}

public class AiCostControlService : IAiCostControlService
{
    private readonly IRedisService? _redis;
    private readonly ILogger<AiCostControlService> _logger;

    // Fallback local quando Redis não está disponível
    private readonly ConcurrentDictionary<string, SlidingWindow> _localRateLimits = new();
    private readonly ConcurrentDictionary<string, DailyBudget> _localBudgets = new();

    public AiCostControlService(
        IRedisService? redis,
        ILogger<AiCostControlService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(
        Guid clientId,
        Guid siteId,
        AIIntegrationSettings settings,
        CancellationToken ct = default)
    {
        if (!settings.CostControlEnabled)
            return true;

        var scopeKey = $"{clientId}:{siteId}";
        var rateKey = $"ai:ratelimit:{scopeKey}";
        var budgetKey = $"ai:budget:{scopeKey}";

        // 1. Rate limit check
        if (_redis?.IsConnected == true)
        {
            var count = await _redis.GetAsync(rateKey);
            var currentCount = string.IsNullOrEmpty(count) ? 0 : int.Parse(count);

            if (currentCount >= settings.RateLimitPerMinute)
            {
                _logger.LogWarning("AI rate limit exceeded for scope {Scope}: {Count}/{Max}/min",
                    scopeKey, currentCount, settings.RateLimitPerMinute);
                return false;
            }

            await _redis.IncrementAsync(rateKey);
            // Se o TTL não existe, seta
            var ttl = await _redis.GetTtlSecondsAsync(rateKey);
            if (ttl <= 0)
                await _redis.SetExpiryAsync(rateKey, 60);
        }
        else
        {
            // Fallback local
            var window = _localRateLimits.GetOrAdd(rateKey, _ => new SlidingWindow());
            var allowed = window.TryIncrement(settings.RateLimitPerMinute);
            if (!allowed)
            {
                _logger.LogWarning("AI rate limit exceeded (local) for scope {Scope}: {Max}/min",
                    scopeKey, settings.RateLimitPerMinute);
                return false;
            }
        }

        // 2. Budget diário check
        if (_redis?.IsConnected == true)
        {
            var used = await _redis.GetAsync(budgetKey);
            var currentUsed = string.IsNullOrEmpty(used) ? 0 : int.Parse(used);

            if (currentUsed >= settings.TokenBudgetDaily)
            {
                _logger.LogWarning("AI daily budget exceeded for scope {Scope}: {Used}/{Budget}",
                    scopeKey, currentUsed, settings.TokenBudgetDaily);
                return false;
            }
        }
        else
        {
            var budget = _localBudgets.GetOrAdd(budgetKey, _ => new DailyBudget());
            if (budget.Used >= settings.TokenBudgetDaily)
            {
                _logger.LogWarning("AI daily budget exceeded (local) for scope {Scope}: {Used}/{Budget}",
                    scopeKey, budget.Used, settings.TokenBudgetDaily);
                return false;
            }
        }

        return true;
    }

    public async Task RecordUsageAsync(
        Guid clientId,
        Guid siteId,
        int tokensUsed,
        CancellationToken ct = default)
    {
        var scopeKey = $"{clientId}:{siteId}";
        var budgetKey = $"ai:budget:{scopeKey}";

        if (_redis?.IsConnected == true)
        {
            await _redis.IncrementAsync(budgetKey);
            var ttl = await _redis.GetTtlSecondsAsync(budgetKey);
            if (ttl <= 0)
                await _redis.SetExpiryAsync(budgetKey, 86400); // 24h
        }
        else
        {
            var budget = _localBudgets.GetOrAdd(budgetKey, _ => new DailyBudget());
            Interlocked.Add(ref budget.Used, tokensUsed);
        }
    }

    private class SlidingWindow
    {
        private long _count;
        private DateTime _windowStart = DateTime.UtcNow;

        public bool TryIncrement(int max)
        {
            var now = DateTime.UtcNow;
            if ((now - _windowStart).TotalMinutes >= 1)
            {
                Interlocked.Exchange(ref _count, 0);
                _windowStart = now;
            }

            var newCount = Interlocked.Increment(ref _count);
            return newCount <= max;
        }
    }

    private class DailyBudget
    {
        public int Used;
    }
}
