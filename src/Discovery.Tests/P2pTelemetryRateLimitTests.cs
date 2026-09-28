using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// O rate-limit server-side de telemetria P2P (5 requisições / 10 min por agente)
/// estava implementado em P2pService mas nunca era chamado — código morto, e o
/// agente tinha toda a lógica de retry para 429 que nunca acontecia. Estes testes
/// cobrem a lógica usada pelo endpoint de ingestão e o fail-open com Redis fora.
/// </summary>
public class P2pTelemetryRateLimitTests
{
    [Test]
    public async Task AllowsUpToFiveRequestsThenLimits()
    {
        var redis = new FakeRedisService { TtlSeconds = 120 };
        var service = new P2pService(null!, null!, null!, redis);
        var agentId = Guid.NewGuid();

        for (var i = 1; i <= 5; i++)
        {
            Assert.That(await service.CheckTelemetryRateLimitAsync(agentId), Is.Zero,
                $"requisição {i} deveria passar");
        }

        var retryAfter = await service.CheckTelemetryRateLimitAsync(agentId);
        Assert.That(retryAfter, Is.GreaterThan(0), "a 6ª requisição na janela deve ser limitada");
    }

    [Test]
    public async Task FailsOpenWhenRedisIsUnavailable()
    {
        // RedisService.IncrementAsync devolve 0 quando o Redis está fora.
        var redis = new FakeRedisService { IncrementResult = 0 };
        var service = new P2pService(null!, null!, null!, redis);

        Assert.That(await service.CheckTelemetryRateLimitAsync(Guid.NewGuid()), Is.Zero);
    }

    private sealed class FakeRedisService : IRedisService
    {
        /// <summary>-1 = incrementa de verdade; >= 0 simula o valor devolvido.</summary>
        public long IncrementResult { get; set; } = -1;

        public int TtlSeconds { get; set; } = 600;

        private long _counter;

        public bool IsConnected => true;

        public Task<long> IncrementAsync(string key)
        {
            if (IncrementResult >= 0) return Task.FromResult(IncrementResult);
            _counter++;
            return Task.FromResult(_counter);
        }

        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);

        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(TtlSeconds);

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);

        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(amount);

        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;

        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;

        public Task PublishAsync(string channel, string message) => Task.CompletedTask;

        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds)
            => Task.FromResult(true);
    }
}
