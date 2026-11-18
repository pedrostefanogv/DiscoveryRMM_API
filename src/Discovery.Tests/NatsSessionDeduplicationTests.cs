using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes para detecção de conexão duplicada (Fase 1) e auditoria (Fase 2).
/// </summary>
public class NatsSessionDeduplicationTests
{
    private sealed class FakeTokenRepository(List<AgentToken> tokens) : IAgentTokenRepository
    {
        public Task<AgentToken?> GetByIdAsync(Guid id)
            => Task.FromResult(tokens.FirstOrDefault(t => t.Id == id));

        public Task<AgentToken?> GetByTokenHashAsync(string tokenHash)
            => Task.FromResult(tokens.FirstOrDefault(t => t.TokenHash == tokenHash));

        public Task<IEnumerable<AgentToken>> GetByAgentIdAsync(Guid agentId)
            => Task.FromResult<IEnumerable<AgentToken>>(tokens.Where(t => t.AgentId == agentId).ToList());

        public Task<AgentToken> CreateAsync(AgentToken token)
        {
            tokens.Add(token);
            return Task.FromResult(token);
        }

        public Task UpdateLastUsedAsync(Guid id) => Task.CompletedTask;

        public Task UpdateLastNatsConnectedAsync(Guid id) => Task.CompletedTask;

        public Task RevokeAsync(Guid id)
        {
            var t = tokens.FirstOrDefault(x => x.Id == id);
            if (t is not null) t.RevokedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public Task RevokeAllByAgentIdAsync(Guid agentId)
        {
            foreach (var t in tokens.Where(x => x.AgentId == agentId))
                t.RevokedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentRepository(Agent? agent = null) : IAgentRepository
    {
        private readonly Agent _agent = agent ?? new Agent { Id = Guid.NewGuid(), SiteId = Guid.NewGuid() };
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult(id == _agent.Id ? _agent : null)!;
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>(ids.Contains(_agent.Id) ? [_agent] : []);

        // Stubs for unused members
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotImplementedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotImplementedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotImplementedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotImplementedException();
        public Task UpdateAsync(Agent agent) => throw new NotImplementedException();
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotImplementedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Agent>> GetOnlineIncludingTrashedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Agent?> GetByIdForDecommissionAsync(Guid id) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotImplementedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotImplementedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// Simula operações Redis em memória para testes.
    /// </summary>
    private sealed class FakeRedisService : IRedisService
    {
        private readonly Dictionary<string, string> _store = new();
        private readonly Dictionary<string, DateTime> _expiries = new();

        public bool IsConnected => true;
        public Task<string?> GetAsync(string key)
        {
            if (_expiries.TryGetValue(key, out var expiry) && DateTime.UtcNow > expiry)
            {
                _store.Remove(key);
                _expiries.Remove(key);
                return Task.FromResult<string?>(null);
            }
            return Task.FromResult(_store.TryGetValue(key, out var val) ? val : null);
        }
        public Task<long> IncrementAsync(string key) => throw new NotImplementedException();
        public Task<long> IncrementByAsync(string key, long amount) => throw new NotImplementedException();
        public Task SetAsync(string key, string value, int expirySeconds = 3600)
        {
            _store[key] = value;
            if (expirySeconds > 0)
                _expiries[key] = DateTime.UtcNow.AddSeconds(expirySeconds);
            return Task.CompletedTask;
        }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => throw new NotImplementedException();
        public Task<int> GetTtlSecondsAsync(string key) => throw new NotImplementedException();
        public Task DeleteAsync(string key)
        {
            _store.Remove(key);
            _expiries.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeleteByPrefixAsync(string prefix) => throw new NotImplementedException();
        public Task PublishAsync(string channel, string message) => throw new NotImplementedException();
        public Task SubscribeAsync(string channel, Action<string, string> handler) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000) => throw new NotImplementedException();
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds)
        {
            if (_store.ContainsKey(key) && (!_expiries.TryGetValue(key, out var exp) || DateTime.UtcNow <= exp))
                return Task.FromResult(false);

            _store[key] = value;
            if (expirySeconds > 0)
                _expiries[key] = DateTime.UtcNow.AddSeconds(expirySeconds);
            return Task.FromResult(true);
        }
    }

    private AgentTokenAuthService CreateService(List<AgentToken> tokens, Agent? agent = null, FakeRedisService? redis = null)
    {
        redis ??= new FakeRedisService();
        return new AgentTokenAuthService(
            new FakeTokenRepository(tokens),
            new FakeAgentRepository(agent),
            redis);
    }

    // ── Testes ────────────────────────────────────────────────────────────────

    [Test]
    public async Task TryAcquireNatsSession_FirstAttempt_Succeeds()
    {
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var service = CreateService([]);

        var acquired = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "test_nkey", TimeSpan.FromMinutes(5));

        Assert.That(acquired, Is.True, "Primeira tentativa de adquirir sessão deve ter sucesso.");
    }

    [Test]
    public async Task TryAcquireNatsSession_SameAgentReacquires_Succeeds()
    {
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var redis = new FakeRedisService();
        var service = CreateService([], redis: redis);

        var first = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "test_nkey", TimeSpan.FromMinutes(5));
        Assert.That(first, Is.True);

        // CONTRATO (corrigido 2026-11-18): a sessão NATS é vinculada ao AGENTE.
        // Mesmo tokenId + MESMO agente + MESMO nkey (ex: reconexão/troca de
        // transporte nats→wss mantendo as credenciais) — renova (sobrescreve).
        var second = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "test_nkey", TimeSpan.FromMinutes(5));
        Assert.That(second, Is.True, "Mesmo agente com o mesmo nkey deve renovar a sessão.");

        // Mesmo agente com NKEY DIFERENTE — TAMBÉM RENOVA.
        //
        // Este é o cenário REAL de reconexão: o NATS gera um user_nkey efêmero
        // novo a cada conexão, então exigir o mesmo nkey rejeitava todo reconnect
        // legítimo ("Token already in use by another connection.") e prendia o
        // agente num loop de reconexão. O nkey não identifica o cliente.
        var reconnected = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "brand_new_ephemeral_nkey", TimeSpan.FromMinutes(5));
        Assert.That(reconnected, Is.True,
            "Reconnect do mesmo agente com nkey efêmero novo deve renovar a sessão.");
    }

    [Test]
    public async Task TryAcquireNatsSession_SameAgentRotatingEphemeralNkey_StaysAcquired()
    {
        // Regressão do incidente 2026-11-18: o agente reconectava a cada ~10s,
        // cada tentativa trazendo um user_nkey novo (o NATS gera um por conexão).
        // Antes da correção, apenas a PRIMEIRA adquiria e todas as seguintes
        // eram recusadas — o agente nunca estabilizava como online.
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var service = CreateService([]);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var acquired = await service.TryAcquireNatsSessionAsync(
                tokenId, agentId, $"ephemeral_nkey_{attempt}", TimeSpan.FromMinutes(5));
            Assert.That(acquired, Is.True,
                $"Tentativa de reconexão #{attempt} deveria renovar a sessão do mesmo agente.");
        }
    }

    [Test]
    public async Task TryAcquireNatsSession_SameAgentReconnect_RefreshesTtl()
    {
        // O reconnect deve RENOVAR o TTL: sem isso a trava expiraria no meio de
        // uma sessão viva e abriria janela para conexão duplicada.
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var redis = new FakeRedisService();
        var service = CreateService([], redis: redis);

        await service.TryAcquireNatsSessionAsync(tokenId, agentId, "nkey_a", TimeSpan.FromSeconds(30));

        // Reconecta com TTL maior — o valor gravado deve refletir o novo TTL.
        var renewed = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "nkey_b", TimeSpan.FromMinutes(10));
        Assert.That(renewed, Is.True);

        var stored = await redis.GetAsync($"nats:session:token:{tokenId}");
        Assert.That(stored, Is.Not.Null, "A sessão deve continuar registrada após o reconnect.");
        Assert.That(stored, Does.Contain("nkey_b"),
            "O valor da sessão deve ser sobrescrito com o nkey da conexão mais recente (auditoria).");
    }

    [Test]
    public async Task TryAcquireNatsSession_DifferentAgentWithSameToken_Fails()
    {
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var redis = new FakeRedisService();
        var service = CreateService([], redis: redis);

        var first = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "test_nkey", TimeSpan.FromMinutes(5));
        Assert.That(first, Is.True);

        // Mesmo tokenId mas OUTRO agente (roubo/reutilização indevida) — deve falhar.
        // ESTA é a proteção anti-hijack que precisa sobreviver à correção: o que
        // separa "mesmo agente reconectando" de "token clonado" é o agentId, não
        // o user_nkey (que é efêmero e muda a cada conexão).
        var stolen = await service.TryAcquireNatsSessionAsync(
            tokenId, otherAgentId, "other_nkey", TimeSpan.FromMinutes(5));
        Assert.That(stolen, Is.False, "Token em uso por outro agente deve ser rejeitado.");
    }

    [Test]
    public async Task TryAcquireNatsSession_DifferentAgentCannotHijackEvenWithMatchingNkey()
    {
        // Reforço: nem reapresentando o MESMO nkey um agente diferente assume a
        // sessão. Garante que a remoção do critério de nkey não abriu brecha.
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService([]);

        Assert.That(await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "shared_nkey", TimeSpan.FromMinutes(5)), Is.True);

        Assert.That(await service.TryAcquireNatsSessionAsync(
            tokenId, otherAgentId, "shared_nkey", TimeSpan.FromMinutes(5)), Is.False,
            "Outro agente não pode assumir a sessão mesmo reapresentando o mesmo nkey.");
    }

    [Test]
    public async Task TryAcquireNatsSession_AfterRelease_Succeeds()
    {
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var redis = new FakeRedisService();
        var service = CreateService([], redis: redis);

        await service.TryAcquireNatsSessionAsync(tokenId, agentId, "nkey", TimeSpan.FromMinutes(5));
        await service.ReleaseNatsSessionAsync(tokenId);

        var reacquired = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "nkey2", TimeSpan.FromMinutes(5));
        Assert.That(reacquired, Is.True, "Após liberar a sessão, deve ser possível readquiri-la.");
    }

    [Test]
    public async Task TryAcquireNatsSession_DifferentTokens_Succeed()
    {
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var redis = new FakeRedisService();
        var service = CreateService([], redis: redis);

        var a = await service.TryAcquireNatsSessionAsync(tokenA, agentId, "nkey_a", TimeSpan.FromMinutes(5));
        var b = await service.TryAcquireNatsSessionAsync(tokenB, agentId, "nkey_b", TimeSpan.FromMinutes(5));

        Assert.That(a, Is.True);
        Assert.That(b, Is.True, "Tokens diferentes devem poder adquirir sessões independentes.");
    }

    [Test]
    public async Task TryAcquireNatsSession_ExpiredSession_CanBeReacquired()
    {
        var tokenId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var redis = new FakeRedisService();
        var service = CreateService([], redis: redis);

        // Sessão com TTL de 1 segundo
        await service.TryAcquireNatsSessionAsync(tokenId, agentId, "nkey", TimeSpan.FromSeconds(1));

        // Espera a sessão expirar (TTL 1s)
        await Task.Delay(1500);

        var reacquired = await service.TryAcquireNatsSessionAsync(
            tokenId, agentId, "nkey2", TimeSpan.FromMinutes(5));
        Assert.That(reacquired, Is.True, "Após expiração da sessão, deve ser possível readquiri-la.");
    }
}
