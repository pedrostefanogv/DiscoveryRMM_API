using Discovery.Api.Services;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// O progresso do reprocessamento passou a ser publicado no Redis (com fallback em
/// memoria), para que um <c>GET /agent-labels/reprocess/{jobId}</c> que caia em outra
/// replica da API encontre o job em vez de responder 404.
/// </summary>
public class LabelReprocessBackgroundServiceTests
{
    [Test]
    public async Task Status_IsVisibleFromAnotherInstance_ViaRedis()
    {
        var redis = new InMemoryRedisService();
        var first = new LabelReprocessBackgroundService(
            new UnusedServiceProvider(), redis, NullLogger<LabelReprocessBackgroundService>.Instance);

        var jobId = await first.EnqueueAsync("user:1");

        // Segunda instancia: sem estado em memoria (simula outra replica).
        var second = new LabelReprocessBackgroundService(
            new UnusedServiceProvider(), redis, NullLogger<LabelReprocessBackgroundService>.Instance);

        var status = await second.GetStatusAsync(jobId);

        Assert.That(status, Is.Not.Null, "O job deve ser visivel em outra replica via Redis.");
        Assert.That(status!.JobId, Is.EqualTo(jobId));
        Assert.That(status.State, Is.EqualTo("Queued"));
    }

    [Test]
    public async Task Status_WhenRedisFails_FallsBackToMemory()
    {
        var service = new LabelReprocessBackgroundService(
            new UnusedServiceProvider(), new ThrowingRedisService(), NullLogger<LabelReprocessBackgroundService>.Instance);

        var jobId = await service.EnqueueAsync("user:1");
        var status = await service.GetStatusAsync(jobId);

        Assert.That(status, Is.Not.Null, "Falha de Redis nao pode esconder o job local.");
        Assert.That(status!.JobId, Is.EqualTo(jobId));
    }

    [Test]
    public async Task Status_WhenUnknownJob_ReturnsNull()
    {
        var service = new LabelReprocessBackgroundService(
            new UnusedServiceProvider(), new InMemoryRedisService(), NullLogger<LabelReprocessBackgroundService>.Instance);

        Assert.That(await service.GetStatusAsync("nao-existe"), Is.Null);
    }

    private sealed class UnusedServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class InMemoryRedisService : IRedisService
    {
        private readonly Dictionary<string, string> _store = [];

        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult(_store.TryGetValue(key, out var value) ? value : null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    private sealed class ThrowingRedisService : IRedisService
    {
        public bool IsConnected => false;
        public Task<string?> GetAsync(string key) => Task.FromException<string?>(new InvalidOperationException("redis fora do ar"));
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.FromException(new InvalidOperationException("redis fora do ar"));
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(false);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key) => Task.FromException(new InvalidOperationException("redis fora do ar"));
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }
}
