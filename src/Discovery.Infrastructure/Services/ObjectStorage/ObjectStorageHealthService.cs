using System.Diagnostics;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services.ObjectStorage;

/// <summary>Resultado de uma verificacao ativa (HEAD bucket) do storage.</summary>
public sealed record ObjectStorageHealthSnapshot(
    bool? Reachable,
    bool? BucketReachable,
    int? LatencyMs,
    bool ConfigurationValid,
    DateTime CheckedAtUtc,
    DateTime NextCheckAfterUtc,
    bool Throttled,
    int ConsecutiveFailures,
    string[] Errors);

/// <summary>
/// Verificacao ativa do object storage, desenhada para NAO sobrecarregar a API
/// do S3 (especialmente quando o storage e externo):
///
/// 1. Usa apenas HEAD bucket (BucketExistsAsync) — nunca ListObjects/varredura.
/// 2. Cache em memoria: o endpoint do dashboard le o snapshot cacheado e NUNCA
///    chama o S3 quando o cache esta quente.
/// 3. Single-flight: N abas/usuarios simultaneos geram UM probe.
/// 4. Circuit breaker: apos falhas consecutivas o probe e espacado
///    (1min -> 5min -> 30min) em vez de martelar um endpoint fora do ar.
/// 5. Timeout curto e resultado estruturado: falha nao vira erro 500.
/// </summary>
public sealed class ObjectStorageHealthService(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    IConfiguration configuration,
    ILogger<ObjectStorageHealthService> logger)
{
    internal const string CacheKey = "object-storage:health";
    private const int DefaultCacheSeconds = 300;
    private const int DefaultProbeTimeoutSeconds = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _consecutiveFailures;

    public async Task<ObjectStorageHealthSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue<ObjectStorageHealthSnapshot>(CacheKey, out var cached) && cached is not null)
            return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check: outra request pode ter populado o cache enquanto esperavamos.
            if (cache.TryGetValue<ObjectStorageHealthSnapshot>(CacheKey, out cached) && cached is not null)
                return cached;

            var snapshot = await ProbeAsync(ct).ConfigureAwait(false);
            var ttl = snapshot.NextCheckAfterUtc - DateTime.UtcNow;
            cache.Set(CacheKey, snapshot, ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(5));
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<ObjectStorageHealthSnapshot> ProbeAsync(CancellationToken ct)
    {
        var timeoutSeconds = Math.Clamp(
            configuration.GetValue<int?>("ObjectStorage:Telemetry:ProbeTimeoutSeconds") ?? DefaultProbeTimeoutSeconds,
            1,
            15);
        var cacheSeconds = Math.Clamp(
            configuration.GetValue<int?>("ObjectStorage:Telemetry:CacheSeconds") ?? DefaultCacheSeconds,
            30,
            3600);

        var sw = Stopwatch.StartNew();
        ObjectStorageTestResult result;
        try
        {
            // Guarda arquitetural (SyncScopeDisposalGuardTests): escopo assincrono
            // evita InvalidOperationException ao descartar servicos IAsyncDisposable.
            await using var scope = scopeFactory.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IObjectStorageProviderFactory>();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            result = await factory.TestConnectionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Probe do object storage falhou");
            result = new ObjectStorageTestResult(
                Success: false,
                ConfigurationValid: false,
                BucketReachable: false,
                Errors: [ex.Message],
                LatencyMs: 0);
        }

        sw.Stop();

        var failures = result.Success
            ? Interlocked.Exchange(ref _consecutiveFailures, 0)
            : Interlocked.Increment(ref _consecutiveFailures);

        // Backoff calculado APOS o probe, com a contagem ja atualizada.
        var backoff = failures switch
        {
            <= 1 => TimeSpan.Zero,
            2 => TimeSpan.FromMinutes(1),
            3 => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromMinutes(30)
        };

        var now = DateTime.UtcNow;

        return new ObjectStorageHealthSnapshot(
            Reachable: result.Success,
            BucketReachable: result.BucketReachable,
            LatencyMs: (int)(result.LatencyMs > 0 ? result.LatencyMs : sw.ElapsedMilliseconds),
            ConfigurationValid: result.ConfigurationValid,
            CheckedAtUtc: now,
            NextCheckAfterUtc: now.AddSeconds(cacheSeconds).Add(backoff),
            Throttled: !result.Success && backoff > TimeSpan.Zero,
            ConsecutiveFailures: failures,
            Errors: result.Errors);
    }
}
