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

    /// <summary>
    /// Devolve um slot de rate limit adquirido por TryAcquireAsync quando a
    /// requisição falhou antes de consumir o LLM (ex.: erro de rede, LLM
    /// indisponível). Evita que erros transientes consumam o quota do usuário.
    /// </summary>
    Task ReleaseAsync(
        Guid clientId,
        Guid siteId,
        AIIntegrationSettings settings,
        CancellationToken ct = default);
}

public sealed class AiCostControlLocalState
{
    public ConcurrentDictionary<string, AiCostControlService.SlidingWindow> RateLimits { get; } = new();
    public ConcurrentDictionary<string, AiCostControlService.DailyBudget> Budgets { get; } = new();
}

public class AiCostControlService : IAiCostControlService
{
    private readonly IRedisService? _redis;
    private readonly ILogger<AiCostControlService> _logger;

    // Fallback local quando Redis não está disponível (compartilhado via Singleton AiCostControlLocalState).
    private readonly ConcurrentDictionary<string, SlidingWindow> _localRateLimits;
    private readonly ConcurrentDictionary<string, DailyBudget> _localBudgets;

    private readonly IAiMonthlyCostReader? _monthlyCostReader;

    public AiCostControlService(
        IRedisService? redis,
        ILogger<AiCostControlService> logger,
        IAiMonthlyCostReader? monthlyCostReader = null,
        AiCostControlLocalState? localState = null)
    {
        _redis = redis;
        _logger = logger;
        _monthlyCostReader = monthlyCostReader;
        var state = localState ?? new AiCostControlLocalState();
        _localRateLimits = state.RateLimits;
        _localBudgets = state.Budgets;
    }

    public async Task<bool> TryAcquireAsync(
        Guid clientId,
        Guid siteId,
        AIIntegrationSettings settings,
        CancellationToken ct = default)
    {
        // 0. Orçamento mensal (USD) por cliente — painel de custo IA. Fail-open
        // quando o budget não está configurado ou o leitor de custo indisponível.
        if (settings.BudgetUsdMonthly is double monthlyBudget && monthlyBudget > 0 && _monthlyCostReader is not null)
        {
            var monthCost = await _monthlyCostReader.GetMonthToDateCostUsdAsync(clientId, ct);
            if (monthCost is double currentMonthCost && currentMonthCost >= monthlyBudget)
            {
                _logger.LogWarning(
                    "AI monthly budget exceeded for client {ClientId}: {Cost:F2}/{Budget:F2} USD — bloqueando novas chamadas",
                    clientId, currentMonthCost, monthlyBudget);
                return false;
            }
        }

        if (!settings.CostControlEnabled)
            return true;

        var scopeKey = $"{clientId}:{siteId}";
        var rateKey = $"ai:ratelimit:{scopeKey}";
        var budgetKey = $"ai:budget:{scopeKey}";

        // 1. Budget diário ANTES do rate limit: consumir um slot de rate e depois
        // bloquear por budget castigava o usuário duas vezes (o slot ficava gasto
        // por uma requisição que nunca chegou ao provedor).
        if (_redis?.IsConnected == true)
        {
            var used = await _redis.GetAsync(budgetKey);
            var currentUsed = long.TryParse(used, out var parsedUsed) ? parsedUsed : 0;

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
            var usedToday = budget.GetUsedToday();
            if (usedToday >= settings.TokenBudgetDaily)
            {
                _logger.LogWarning("AI daily budget exceeded (local) for scope {Scope}: {Used}/{Budget}",
                    scopeKey, usedToday, settings.TokenBudgetDaily);
                return false;
            }
        }

        // 2. Rate limit (consome o slot somente quando a requisição vai prosseguir).
        if (_redis?.IsConnected == true)
        {
            var count = await _redis.GetAsync(rateKey);
            var currentCount = long.TryParse(count, out var parsedCount) ? parsedCount : 0;

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

        return true;
    }

    public async Task RecordUsageAsync(
        Guid clientId,
        Guid siteId,
        int tokensUsed,
        CancellationToken ct = default)
    {
        if (tokensUsed <= 0) return;

        var scopeKey = $"{clientId}:{siteId}";
        var budgetKey = $"ai:budget:{scopeKey}";

        if (_redis?.IsConnected == true)
        {
            // INCRBY com o valor real de tokens (antes: IncrementAsync
            // somava apenas 1 — o budget diário nunca refletia o consumo).
            await _redis.IncrementByAsync(budgetKey, tokensUsed);
            var ttl = await _redis.GetTtlSecondsAsync(budgetKey);
            if (ttl <= 0)
                await _redis.SetExpiryAsync(budgetKey, 86400); // 24h
        }
        else
        {
            var budget = _localBudgets.GetOrAdd(budgetKey, _ => new DailyBudget());
            budget.AddUsage(tokensUsed);
        }
    }

    public async Task ReleaseAsync(
        Guid clientId,
        Guid siteId,
        AIIntegrationSettings settings,
        CancellationToken ct = default)
    {
        if (!settings.CostControlEnabled)
            return;

        var scopeKey = $"{clientId}:{siteId}";
        var rateKey = $"ai:ratelimit:{scopeKey}";

        if (_redis?.IsConnected == true)
        {
            // Só decrementa se a chave ainda existir e for > 0, evitando criar chave "-1" sem TTL.
            var raw = await _redis.GetAsync(rateKey);
            if (long.TryParse(raw, out var current) && current > 0)
            {
                await _redis.IncrementByAsync(rateKey, -1);
                var ttl = await _redis.GetTtlSecondsAsync(rateKey);
                if (ttl <= 0)
                    await _redis.SetExpiryAsync(rateKey, 60);
            }
            return;
        }

        // Fallback local: decrementa o sliding window se possível.
        var window = _localRateLimits.GetOrAdd(rateKey, _ => new SlidingWindow());
        window.Decrement();
    }

    public class SlidingWindow
    {
        private readonly object _lock = new();
        private long _count;
        private DateTime _windowStart = DateTime.UtcNow;

        public bool TryIncrement(int max)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if ((now - _windowStart).TotalMinutes >= 1)
                {
                    _count = 0;
                    _windowStart = now;
                }

                if (_count >= max)
                    return false;

                _count++;
                return true;
            }
        }

        /// <summary>
        /// Devolve um slot ao window (usado quando a requisição falhou antes
        /// de consumir o LLM). Não deixa o contador negativo.
        /// </summary>
        public void Decrement()
        {
            lock (_lock)
            {
                if (_count > 0) _count--;
            }
        }
    }

    public class DailyBudget
    {
        private readonly object _lock = new();
        private DateOnly _utcDate = DateOnly.FromDateTime(DateTime.UtcNow);
        private int _used;

        public int GetUsedToday()
        {
            lock (_lock)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (_utcDate != today)
                {
                    _utcDate = today;
                    _used = 0;
                }
                return _used;
            }
        }

        public void AddUsage(int tokens)
        {
            lock (_lock)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (_utcDate != today)
                {
                    _utcDate = today;
                    _used = 0;
                }
                _used += tokens;
            }
        }
    }
}