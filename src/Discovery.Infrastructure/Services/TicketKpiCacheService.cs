using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço de cache para KPIs de tickets usando Redis.
/// TTL de 60 segundos com invalidação on-write.
/// </summary>
public class TicketKpiCacheService : ITicketKpiCacheService
{
    private readonly IRedisService _redis;
    private readonly ILogger<TicketKpiCacheService> _logger;
    private const int CacheTtlSeconds = 60;
    private const string KeyPrefix = "tickets:kpi";

    public TicketKpiCacheService(IRedisService redis, ILogger<TicketKpiCacheService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<TicketKpiResult> GetOrComputeAsync(
        Guid? clientId,
        Guid? departmentId,
        DateTime? since,
        Func<Task<TicketKpiResult>> factory,
        CancellationToken ct = default)
    {
        var cacheKey = BuildKey(clientId, departmentId, since);

        try
        {
            var cached = await _redis.GetAsync(cacheKey);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var result = JsonSerializer.Deserialize<TicketKpiResult>(cached);
                if (result is not null)
                {
                    _logger.LogDebug("KPI cache hit for key {Key}", cacheKey);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read KPI cache, falling back to compute");
        }

        var computed = await factory();
        await SetCacheAsync(cacheKey, computed, ct);
        return computed;
    }

    public async Task InvalidateAsync(Guid clientId, CancellationToken ct = default)
    {
        try
        {
            var pattern = $"{KeyPrefix}:{clientId}:*";
            await _redis.DeleteByPrefixAsync(pattern);
            _logger.LogDebug("Invalidated KPI cache for client {ClientId}", clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate KPI cache for client {ClientId}", clientId);
        }
    }

    public async Task InvalidateAllAsync(CancellationToken ct = default)
    {
        try
        {
            await _redis.DeleteByPrefixAsync($"{KeyPrefix}:");
            _logger.LogDebug("Invalidated all KPI caches");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate all KPI caches");
        }
    }

    private async Task SetCacheAsync(string key, TicketKpiResult result, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(result);
            await _redis.SetAsync(key, json, CacheTtlSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write KPI cache");
        }
    }

    private static string BuildKey(Guid? clientId, Guid? departmentId, DateTime? since)
    {
        var parts = new List<string> { KeyPrefix };
        parts.Add(clientId?.ToString() ?? "all");
        parts.Add(departmentId?.ToString() ?? "all");
        parts.Add(since?.ToString("yyyyMMdd") ?? "all");
        return string.Join(":", parts);
    }
}
