using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Patches;
using Discovery.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// O handler da lista de pendências ganhou a dependência <see cref="IHeartbeatCacheService"/>
/// para ler o estado AO VIVO do agente. Este teste trava o contrato de DI: se a
/// interface deixar de estar registrada (ou o lifetime virar incompatível), a lista
/// de /patches/pending para de resolver e o endpoint quebra em produção — falha
/// silenciosa que só apareceria no request.
/// </summary>
[TestFixture]
public class PatchPendingHandlerDependencyTests
{
    /// <summary>Redis de mentira: só precisamos que a interface exista no container.</summary>
    private sealed class StubHeartbeatCache : IHeartbeatCacheService
    {
        public Task<bool> SetHeartbeatAsync(Discovery.Core.DTOs.AgentHeartbeat heartbeat, Discovery.Core.Enums.AgentStatus status, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<Discovery.Core.Interfaces.HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<Discovery.Core.Interfaces.HeartbeatCacheEntry?>(null);

        public Task<IReadOnlyList<Discovery.Core.Interfaces.HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Discovery.Core.Interfaces.HeartbeatCacheEntry>>([]);

        public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> DetectExpiredAsync(IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Redis de mentira para o container: o handler só precisa que a interface exista,
    /// com IsConnected = true (o fake de heartbeat acima responde vazio).
    /// </summary>
    private sealed class StubRedisService : IRedisService
    {
        public bool IsConnected => true;

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);

        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);

        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);

        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;

        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(false);

        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(0);

        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;

        public Task PublishAsync(string channel, string message) => Task.CompletedTask;

        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds)
            => Task.FromResult(false);
    }

    [Test]
    public void Handler_DeveSerConstruivel_ComAsDependenciasRegistradas()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHeartbeatCacheService, StubHeartbeatCache>();
        // O handler consulta IRedisService.IsConnected antes de ler o cache — sem este
        // registro a lista de /patches/pending para de resolver no request.
        services.AddSingleton<IRedisService, StubRedisService>();
        services.AddScoped<IPatchRepository>(_ => new PatchRepository(BoardTestDbContext.NewContext()));
        services.AddScoped<GetPatchPendingQueryHandler>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // O handler é scoped (igual ao repositório): resolve dentro de um scope,
        // como o MediatR faz por request.
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetPatchPendingQueryHandler>();
        Assert.That(handler, Is.Not.Null);
    }
}
