using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services.ObjectStorage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Garante o contrato anti-sobrecarga do card de S3: cache com TTL, single-flight
/// e circuit breaker. Se estes testes quebrarem, o dashboard pode voltar a
/// martelar a API do storage (critico quando o S3 e externo).
/// </summary>
public class ObjectStorageHealthServiceTests
{
    private sealed class CountingFactory : IObjectStorageProviderFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public bool Success { get; set; } = true;
        public int DelayMs { get; set; }

        public IObjectStorageService CreateObjectStorageService() => throw new NotSupportedException();

        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());

        public async Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            if (DelayMs > 0)
                await Task.Delay(DelayMs, cancellationToken);

            return Success
                ? new ObjectStorageTestResult(true, true, true, [], 12)
                : new ObjectStorageTestResult(false, true, false, ["unreachable"], 12);
        }
    }

    private static (ObjectStorageHealthService Service, CountingFactory Factory, IServiceProvider Provider) Build(
        CountingFactory? factory = null,
        int cacheSeconds = 300,
        int probeTimeoutSeconds = 3)
    {
        factory ??= new CountingFactory();

        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddSingleton<IObjectStorageProviderFactory>(factory);
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ObjectStorage:Telemetry:CacheSeconds"] = cacheSeconds.ToString(),
                ["ObjectStorage:Telemetry:ProbeTimeoutSeconds"] = probeTimeoutSeconds.ToString()
            })
            .Build());

        var provider = services.BuildServiceProvider();
        var service = new ObjectStorageHealthService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMemoryCache>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<ObjectStorageHealthService>.Instance);

        return (service, factory, provider);
    }

    [Test]
    public async Task Cache_ReusesSnapshotWithinTtl_AndProbesOnlyOnce()
    {
        var (service, factory, _) = Build();

        var first = await service.GetAsync();
        var second = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(factory.Calls, Is.EqualTo(1));
            Assert.That(first.Reachable, Is.True);
            Assert.That(second.CheckedAtUtc, Is.EqualTo(first.CheckedAtUtc));
        });
    }

    [Test]
    public async Task SingleFlight_CollapsesConcurrentProbes()
    {
        var factory = new CountingFactory { DelayMs = 150 };
        var (service, _, _) = Build(factory);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 25).Select(_ => service.GetAsync()));

        Assert.Multiple(() =>
        {
            Assert.That(factory.Calls, Is.EqualTo(1));
            Assert.That(results.All(result => result.Reachable == true), Is.True);
        });
    }

    [Test]
    public async Task Failures_BackOffAndThrottle()
    {
        var factory = new CountingFactory { Success = false };
        var (service, _, provider) = Build(factory);
        var cache = provider.GetRequiredService<IMemoryCache>();

        var first = await service.GetAsync();
        Assert.Multiple(() =>
        {
            Assert.That(first.ConsecutiveFailures, Is.EqualTo(1));
            Assert.That(first.Throttled, Is.False);
            Assert.That(first.Reachable, Is.False);
        });

        // Forca novo probe (simula expiracao do cache) para acumular a 2a falha.
        cache.Remove(ObjectStorageHealthService.CacheKey);
        var second = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second.ConsecutiveFailures, Is.EqualTo(2));
            Assert.That(second.Throttled, Is.True);
            Assert.That(second.NextCheckAfterUtc - second.CheckedAtUtc, Is.GreaterThan(TimeSpan.FromSeconds(60)));
        });
    }

    [Test]
    public async Task ProbeTimeout_DoesNotThrow()
    {
        var factory = new CountingFactory { DelayMs = 5000 };
        var (service, _, _) = Build(factory, probeTimeoutSeconds: 1);

        var snapshot = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Reachable, Is.False);
            Assert.That(snapshot.Errors, Is.Not.Empty);
        });
    }
}
