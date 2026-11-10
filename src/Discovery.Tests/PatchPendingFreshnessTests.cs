using Discovery.Core.Cqrs.Patches;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Patches;
using Discovery.Infrastructure.Repositories;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Frescor/status AO VIVO na lista de pendências (<see cref="GetPatchPendingQueryHandler"/>).
///
/// <para>
/// A lista é montada a partir do snapshot/rollup de Windows Update, que congela o estado
/// do agente no instante em que foi gravado e pode ter dias. O handler precisa cruzar esse
/// material com o estado atual (agents.status / agents.last_seen_at) e com a chave de
/// heartbeat no Redis — que é a ÚNICA fonte de verdade de "online agora", já que o banco
/// só é escrito nas transições (um agente online há horas mantém o instante do início da
/// sessão em last_seen_at).
/// </para>
/// </summary>
[TestFixture]
public class PatchPendingFreshnessTests
{
    /// <summary>
    /// Conexão de mentira com o Redis. O handler consulta
    /// <see cref="IRedisService.IsConnected"/> ANTES de ler o cache: um cache vazio e um
    /// cache fora do ar respondem a mesma coisa (ninguém online), e sem esse flag seria
    /// impossível dizer se aquilo é RESPOSTA ou SILÊNCIO — o silêncio tem de cair para o
    /// estado persistido, nunca pintar a frota de Offline.
    /// </summary>
    private sealed class FakeRedisService(bool isConnected) : IRedisService
    {
        public bool IsConnected { get; } = isConnected;

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

    /// <summary>
    /// Dublê do cache de heartbeat: responde pela CHAVE INDIVIDUAL dos ids vivos.
    ///
    /// <para>
    /// A varredura por prefixo (GetActiveHeartbeatsAsync) LANÇA de propósito: em
    /// produção o conjunto heartbeat:online:agents está vazio, então qualquer
    /// dependência dela apareceria como falha aqui.
    /// </para>
    /// </summary>
    private sealed class FakeHeartbeatCache(params HeartbeatCacheEntry[] entries) : IHeartbeatCacheService
    {
        public int KeyReadCount { get; private set; }

        public Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException(
                "O conjunto heartbeat:online:agents está vazio em produção — a lista não pode depender da varredura por prefixo.");

        public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
        {
            KeyReadCount++;
            return Task.FromResult(entries.FirstOrDefault(entry => entry.AgentId == agentId));
        }

        public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult(entries.Any(entry => entry.AgentId == agentId));

        public Task<IReadOnlyList<Guid>> DetectExpiredAsync(
            IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>Redis fora do ar: toda leitura degrada para o estado persistido.</summary>
    private sealed class UnavailableHeartbeatCache : IHeartbeatCacheService
    {
        public Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("redis indisponível");

        public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("redis indisponível");

        public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> DetectExpiredAsync(
            IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Semeia UM agente com snapshot de Windows Update ANTIGO (3 dias) e o estado
    /// informado no banco. O snapshot antigo é o cenário real da produção.
    /// </summary>
    private static async Task<Discovery.Infrastructure.Data.DiscoveryDbContext> SeedAgentAsync(
        Guid agentId, AgentStatus persistedStatus, DateTime? persistedLastSeenAt)
    {
        var db = BoardTestDbContext.NewContext();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientId, Name = "Cliente Alfa" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        db.Agents.Add(new Agent
        {
            Id = agentId,
            SiteId = siteId,
            Hostname = "HOST-" + agentId.ToString("N")[..6].ToUpperInvariant(),
            Status = persistedStatus,
            LastSeenAt = persistedLastSeenAt
        });

        // Rollup ANTIGO: o LastSeenAt congelado aqui não pode vencer o estado ao vivo.
        var staleRollup = DateTime.UtcNow.AddDays(-3);
        db.AgentWindowsUpdates.Add(new AgentWindowsUpdate
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UpdateKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
            GroupKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
            Title = "Update",
            IsPresent = true,
            IsPending = true,
            Status = PatchUpdateStatus.Pending,
            ReportedAt = staleRollup,
            FirstSeenAt = staleRollup,
            LastSeenAt = staleRollup,
            CreatedAt = staleRollup,
            UpdatedAt = staleRollup
        });
        db.AgentWindowsUpdateSnapshots.Add(new AgentWindowsUpdateSnapshot
        {
            AgentId = agentId,
            ScanStatus = "ok",
            CollectedAt = staleRollup,
            PendingCount = 1
        });

        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<PatchPendingAgentDto> QuerySingleAsync(
        Guid agentId, AgentStatus persistedStatus, DateTime? persistedLastSeenAt, IHeartbeatCacheService cache)
    {
        var db = await SeedAgentAsync(agentId, persistedStatus, persistedLastSeenAt);
        // Redis CONECTADO: estas fixtures querem medir o veredito ao vivo. O caminho de
        // Redis mudo (IsConnected = false) é coberto por
        // PatchPendingOnlineStatusTests.RedisIndisponivel*.
        var handler = new GetPatchPendingQueryHandler(
            new PatchRepository(db), cache, new FakeRedisService(isConnected: true));
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items, Has.Count.EqualTo(1));
        return result.Value!.Items[0];
    }

    [Test]
    public async Task SnapshotAntigoComHeartbeatVivo_ApareceOnlineEFresco()
    {
        // Cenário de produção: o agente está online AGORA (chave no Redis), mas o
        // rollup de Windows Update é de 3 dias atrás e o banco ainda diz Offline
        // (status só muda na transição, que pode nunca ter sido gravada).
        var agentId = Guid.NewGuid();
        var heartbeatAt = DateTime.UtcNow.AddSeconds(-11);
        var cache = new FakeHeartbeatCache(new HeartbeatCacheEntry
        {
            AgentId = agentId,
            Status = AgentStatus.Online,
            LastHeartbeatAt = heartbeatAt
        });

        var item = await QuerySingleAsync(
            agentId, AgentStatus.Offline, DateTime.UtcNow.AddDays(-3), cache);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo("Online"));
            Assert.That(item.LastSeenAt, Is.EqualTo(heartbeatAt),
                "LastSeenAt deve vir do heartbeat ao vivo, não do rollup antigo.");
        });
    }

    [Test]
    public async Task LastSeenAtAntigoSemHeartbeat_ApareceOffline()
    {
        // Sem chave no Redis E com status persistido Offline: o agente está
        // realmente offline (foi a expiração que gravou o Offline no banco).
        // O last_seen_at velho é repassado como está — é o último sinal conhecido.
        var agentId = Guid.NewGuid();
        var staleSeenAt = DateTime.UtcNow.AddHours(-5);
        var cache = new FakeHeartbeatCache();

        var item = await QuerySingleAsync(agentId, AgentStatus.Offline, staleSeenAt, cache);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo("Offline"));
            Assert.That(item.LastSeenAt, Is.EqualTo(staleSeenAt));
        });
    }

    [Test]
    public async Task LastSeenAtAntigoComStatusPersistidoOnline_NaoInverteParaOffline()
    {
        // Redis RESPONDEU (conectado) e o agente NÃO tem chave: ele está offline AGORA.
        // O status persistido Online é defasado — foi gravado na transição de entrada e
        // nunca reescrito a cada batida. Quem o corrige no banco é a expiração
        // (HeartbeatExpiryBackgroundService), mas a lista não pode esperar por isso:
        // exibir Online aqui é justamente o sintoma de produção (agente cai e a tela só
        // corrige no F5).
        var agentId = Guid.NewGuid();
        var staleSeenAt = DateTime.UtcNow.AddHours(-5);

        var item = await QuerySingleAsync(agentId, AgentStatus.Online, staleSeenAt, new FakeHeartbeatCache());

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo("Offline"));
            // Sem chave viva não existe frescor do Redis: devolvemos o último instante
            // PERSISTIDO, que é o último sinal real que temos do agente.
            Assert.That(item.LastSeenAt, Is.EqualTo(staleSeenAt));
        });
    }

    [Test]
    public async Task StatusPersistidoOnlineSemHeartbeat_ContinuaOnline()
    {
        // Redis conectado e SEM chave para o agente: ele está offline agora e o banco
        // ainda diz Online (status defasado por design). O status persistido só volta a
        // valer quando o Redis fica MUDO — isso é coberto por
        // PatchPendingOnlineStatusTests.RedisIndisponivel*.
        var agentId = Guid.NewGuid();
        var seenAt = DateTime.UtcNow.AddMinutes(-2);
        var cache = new FakeHeartbeatCache();

        var item = await QuerySingleAsync(agentId, AgentStatus.Online, seenAt, cache);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo("Offline"));
            Assert.That(item.LastSeenAt, Is.EqualTo(seenAt));
        });
    }

    [Test]
    public async Task RedisIndisponivel_DegradaParaEstadoPersistidoSemQuebrar()
    {
        // Redis fora do ar não pode derrubar a lista: cai para agents.status/last_seen_at.
        var agentId = Guid.NewGuid();
        var seenAt = DateTime.UtcNow.AddMinutes(-2);

        var item = await QuerySingleAsync(agentId, AgentStatus.Offline, seenAt, new UnavailableHeartbeatCache());

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo("Offline"));
            Assert.That(item.LastSeenAt, Is.EqualTo(seenAt));
        });
    }

    [Test]
    public async Task AgenteSemLastSeenAt_NaoQuebraEDevolveOffline()
    {
        // Agente nunca visto (last_seen_at NULL no banco) e sem chave no Redis: a
        // lista não pode lançar nem inventar frescor — devolve null e Offline.
        var agentId = Guid.NewGuid();
        var db = await SeedAgentAsync(agentId, AgentStatus.Offline, null);
        var handler = new GetPatchPendingQueryHandler(
            new PatchRepository(db), new FakeHeartbeatCache(), new FakeRedisService(isConnected: true));
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        var item = result.Value!.Items.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo("Offline"));
            Assert.That(item.LastSeenAt, Is.Null);
        });
    }

    [Test]
    public async Task PaginaInteira_LeAsChavesIndividuaisUmaUnicaVezPorAgente()
    {
        // Custo: uma leitura de chave POR AGENTE DA PÁGINA, numa única rodada —
        // nunca a varredura por prefixo (que em produção não enxerga ninguém).
        var db = BoardTestDbContext.NewContext();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.Clients.Add(new Client { Id = clientId, Name = "Cliente Alfa" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });

        var now = DateTime.UtcNow;
        var entries = new List<HeartbeatCacheEntry>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            db.Agents.Add(new Agent { Id = id, SiteId = siteId, Hostname = "HOST-" + i, Status = AgentStatus.Offline });
            db.AgentWindowsUpdates.Add(new AgentWindowsUpdate
            {
                Id = Guid.NewGuid(),
                AgentId = id,
                UpdateKey = "kb:KB" + i,
                GroupKey = "kb:KB" + i,
                Title = "Update",
                IsPresent = true,
                IsPending = true,
                Status = PatchUpdateStatus.Pending,
                ReportedAt = now.AddDays(-2),
                FirstSeenAt = now.AddDays(-2),
                LastSeenAt = now.AddDays(-2),
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2)
            });
            db.AgentWindowsUpdateSnapshots.Add(new AgentWindowsUpdateSnapshot
            {
                AgentId = id,
                ScanStatus = "ok",
                CollectedAt = now.AddDays(-2),
                PendingCount = 1
            });
            entries.Add(new HeartbeatCacheEntry { AgentId = id, Status = AgentStatus.Online, LastHeartbeatAt = now });
        }
        await db.SaveChangesAsync();

        var cache = new FakeHeartbeatCache([.. entries]);
        var handler = new GetPatchPendingQueryHandler(
            new PatchRepository(db), cache, new FakeRedisService(isConnected: true));
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items, Has.Count.EqualTo(5));
        Assert.That(result.Value!.Items.All(item => item.Status == "Online"), Is.True);
        Assert.That(cache.KeyReadCount, Is.EqualTo(5));
    }
}
