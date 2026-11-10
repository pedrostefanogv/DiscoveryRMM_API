using Discovery.Core.Cqrs.Patches;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Patches;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Status online do agente na lista de pendências de atualização
/// (<see cref="GetPatchPendingQueryHandler"/>): a decisão de "online agora" vem do
/// Redis, com o status persistido do banco apenas como retaguarda quando o Redis
/// está mudo.
///
/// <para>
/// BUG DE PRODUÇÃO que estes testes travam: <c>agents.status</c> e
/// <c>agents.last_seen_at</c> só são gravados nas TRANSIÇÕES de heartbeat. Um agente
/// que pulsa a cada ~60s passa a maior parte do tempo com Status=Offline e
/// LastSeenAt velho no banco (o TTL da chave expira a cada 180s e o
/// HeartbeatExpiryBackgroundService grava Offline). Decidir "online" pelo banco
/// mostrava a frota inteira como Offline até um F5.
/// </para>
/// </summary>
[TestFixture]
public class PatchPendingOnlineStatusTests
{
    /// <summary>
    /// Conexão de mentira com o Redis: o handler consulta
    /// <see cref="IRedisService.IsConnected"/> ANTES de ler o cache, porque um cache
    /// vazio e um cache fora do ar respondem a mesma coisa (nada online) — e só o
    /// flag de conexão diz se aquilo é resposta ou silêncio.
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
    /// Cache de heartbeat de mentira com o conjunto de agentes ONLINE e um
    /// <see cref="SimulateUnavailable"/> que representa o Redis FORA DO AR.
    ///
    /// <para>
    /// Com <see cref="SimulateUnavailable"/> = true a leitura por id (<c>null</c> por
    /// agente, sem exceção) e a varredura por prefixo (lista vazia) imitam o
    /// HeartbeatCacheService real atrás de um IRedisService desconectado — é esse
    /// "vazio silencioso" que não pode ser confundido com "ninguém online".
    /// </para>
    /// </summary>
    private sealed class OnlineAgentsFakeHeartbeatCache : IHeartbeatCacheService
    {
        private readonly HashSet<Guid> _online;

        public OnlineAgentsFakeHeartbeatCache(params Guid[] online) => _online = [.. online];

        /// <summary>Redis inacessível: NENHUMA leitura devolve agente online.</summary>
        public bool SimulateUnavailable { get; set; }

        /// <summary>Quantas vezes a chave individual foi lida (custo por agente).</summary>
        public int KeyReadCount { get; private set; }

        public Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HeartbeatCacheEntry>>(
                SimulateUnavailable || _online.Count == 0
                    ? []
                    : [.. _online.Select(id => new HeartbeatCacheEntry
                    {
                        AgentId = id,
                        Status = AgentStatus.Online,
                        LastHeartbeatAt = DateTime.UtcNow
                    })]);

        public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
        {
            KeyReadCount++;
            // Redis desconectado devolve null para TODOS (como o serviço real):
            // é exatamente o caso em que a lista não pode concluir "offline".
            if (SimulateUnavailable || !_online.Contains(agentId))
                return Task.FromResult<HeartbeatCacheEntry?>(null);

            return Task.FromResult<HeartbeatCacheEntry?>(new HeartbeatCacheEntry
            {
                AgentId = agentId,
                Status = AgentStatus.Online,
                LastHeartbeatAt = DateTime.UtcNow
            });
        }

        public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult(!SimulateUnavailable && _online.Contains(agentId));

        public Task<IReadOnlyList<Guid>> DetectExpiredAsync(
            IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Redis mudo (fora do ar): toda leitura LANÇA. É o cenário em que a lista tem de
    /// cair para o status persistido — se alguém remover a proteção do handler, os
    /// testes de fallback quebram aqui.
    /// </summary>
    private sealed class UnavailableHeartbeatCache : IHeartbeatCacheService
    {
        public Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("redis indisponível");

        public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("redis indisponível");

        public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default)
            => throw new InvalidOperationException("redis indisponível");

        public Task<IReadOnlyList<Guid>> DetectExpiredAsync(
            IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private static (DiscoveryDbContext Db, Guid ClientId, Guid AgentAId, Guid AgentBId) Seed()
    {
        var db = BoardTestDbContext.NewContext();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentAId = Guid.NewGuid();
        var agentBId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientId, Name = "Cliente Alfa" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        // A está Online e B Offline: o handler deve devolver exatamente o status
        // persistido, sem inferir nada de LastSeenAt.
        db.Agents.Add(new Agent
        {
            Id = agentAId,
            SiteId = siteId,
            Hostname = "HOST-A",
            Status = AgentStatus.Online,
            LastSeenAt = DateTime.UtcNow
        });
        db.Agents.Add(new Agent
        {
            Id = agentBId,
            SiteId = siteId,
            Hostname = "HOST-B",
            Status = AgentStatus.Offline,
            LastSeenAt = DateTime.UtcNow.AddDays(-2)
        });
        db.SaveChanges();

        return (db, clientId, agentAId, agentBId);
    }

    private static AgentWindowsUpdate Pending(Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UpdateKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
        GroupKey = "kb:KB" + Guid.NewGuid().ToString("N")[..8],
        Title = "Update",
        IsPresent = true,
        IsPending = true,
        Status = PatchUpdateStatus.Pending,
        ReportedAt = DateTime.UtcNow,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentWindowsUpdateSnapshot Snapshot(Guid agentId) => new()
    {
        AgentId = agentId,
        ScanStatus = "ok",
        CollectedAt = DateTime.UtcNow,
        PendingCount = 1
    };

    [Test]
    public async Task ListaDePendencias_ExpoeStatusPersistidoPorAgente()
    {
        var (db, _, agentAId, agentBId) = Seed();
        db.AgentWindowsUpdates.Add(Pending(agentAId));
        db.AgentWindowsUpdates.Add(Pending(agentBId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentBId));
        await db.SaveChangesAsync();

        var repo = new PatchRepository(db);
        // Redis MUDO (IsConnected = false): não há resposta confiável, então a decisão
        // cai inteiramente no status persistido — é este o cenário que prova que o
        // status do banco continua sendo usado como retaguarda.
        var handler = new GetPatchPendingQueryHandler(
            repo, new OnlineAgentsFakeHeartbeatCache(), new FakeRedisService(isConnected: false));
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        var items = result.Value!.Items;
        Assert.That(items, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            // Com o Redis mudo, Status e IsOnline espelham o estado persistido: A está
            // Online no banco, B está Offline. LastSeenAt não entra na decisão.
            var itemA = items.Single(item => item.AgentId == agentAId);
            var itemB = items.Single(item => item.AgentId == agentBId);
            Assert.That(itemA.Status, Is.EqualTo("Online"));
            Assert.That(itemA.IsOnline, Is.True);
            Assert.That(itemB.Status, Is.EqualTo("Offline"));
            Assert.That(itemB.IsOnline, Is.False);
        });
    }

    [Test]
    public async Task AgenteSemLinhaDeUpdate_NaoApareceNaLista()
    {
        var (db, _, agentAId, agentBId) = Seed();
        // Só A tem pendência; B existe como agente mas não entra na visão.
        db.AgentWindowsUpdates.Add(Pending(agentAId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId));
        await db.SaveChangesAsync();

        var repo = new PatchRepository(db);
        var handler = new GetPatchPendingQueryHandler(
            repo, new OnlineAgentsFakeHeartbeatCache(), new FakeRedisService(isConnected: true));
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        var items = result.Value!.Items;
        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items.Single().AgentId, Is.EqualTo(agentAId));
        Assert.That(items.Any(item => item.AgentId == agentBId), Is.False);
    }

    [Test]
    public async Task EscopoPorCliente_LimitaListaAoClienteInformado()
    {
        var (db, clientId, agentAId, agentBId) = Seed();
        db.AgentWindowsUpdates.Add(Pending(agentAId));
        db.AgentWindowsUpdates.Add(Pending(agentBId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentBId));
        await db.SaveChangesAsync();

        var repo = new PatchRepository(db);
        var handler = new GetPatchPendingQueryHandler(
            repo, new OnlineAgentsFakeHeartbeatCache(), new FakeRedisService(isConnected: true));

        // Cliente dono dos agentes: os dois aparecem.
        var own = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Client, clientId, null, 25, null, true, []), default);
        Assert.That(own.IsSuccess, Is.True);
        Assert.That(own.Value!.Items, Has.Count.EqualTo(2));

        // Cliente inexistente: nada vaza entre clientes.
        var other = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Client, Guid.NewGuid(), null, 25, null, true, []), default);
        Assert.That(other.IsSuccess, Is.True);
        Assert.That(other.Value!.Items, Is.Empty);
    }

    // ── Decisão de "online agora": Redis manda, banco é retaguarda ────────────

    /// <summary>
    /// Semeia UM agente com o status PERSISTIDO informado e uma pendência, devolvendo
    /// o DTO da lista.
    /// </summary>
    private static async Task<PatchPendingAgentDto> QuerySingleAsync(
        Guid agentId, AgentStatus persistedStatus, IHeartbeatCacheService cache, bool redisConnected = true)
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
            Hostname = "HOST-ONLINE",
            Status = persistedStatus,
            LastSeenAt = DateTime.UtcNow.AddHours(-3)
        });
        db.AgentWindowsUpdates.Add(Pending(agentId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentId));
        await db.SaveChangesAsync();

        var handler = new GetPatchPendingQueryHandler(
            new PatchRepository(db), cache, new FakeRedisService(redisConnected));
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items, Has.Count.EqualTo(1));
        return result.Value!.Items[0];
    }

    /// <summary>
    /// (a) O BUG DE PRODUÇÃO: o banco diz Offline (status gravado na última TRANSIÇÃO,
    /// já defasado) mas a chave de heartbeat está viva no Redis — o agente está online
    /// AGORA e a lista tem de dizer isso.
    ///
    /// <para>
    /// ESTE TESTE FALHA se alguém remover o Redis da decisão: com a decisão voltando a
    /// ser só o banco, <c>IsOnline</c> viraria false e o campo Status voltaria a
    /// "Offline" — exatamente o sintoma reportado em produção.
    /// </para>
    /// </summary>
    [Test]
    public async Task RedisDizOnlineMesmoComStatusPersistidoOffline_ApareceOnline()
    {
        var agentId = Guid.NewGuid();
        var cache = new OnlineAgentsFakeHeartbeatCache(agentId);

        var item = await QuerySingleAsync(agentId, AgentStatus.Offline, cache);

        Assert.Multiple(() =>
        {
            Assert.That(item.IsOnline, Is.True,
                "A chave viva no Redis é a fonte de verdade: o status defasado do banco não pode rebaixar o agente.");
            // O Status do payload acompanha o veredito AO VIVO (mesma consulta): expor
            // "Offline" ao lado de IsOnline=true seria devolver a mesma linha com dois
            // sinais contraditórios — o console mostraria Offline outra vez.
            Assert.That(item.Status, Is.EqualTo("Online"));
        });
    }

    /// <summary>
    /// (b) Agente com status persistido Online e FORA do cache: o Redis respondeu
    /// (conjunto conhecido) e não tem chave para ele — está offline agora, e o banco
    /// defasado não o salva.
    /// </summary>
    [Test]
    public async Task RedisDisponivelSemChaveDoAgente_ApareceOffline()
    {
        var agentId = Guid.NewGuid();
        // Cache disponível e vazio: sabemos que NINGUÉM está online.
        var cache = new OnlineAgentsFakeHeartbeatCache();

        var item = await QuerySingleAsync(agentId, AgentStatus.Online, cache);

        Assert.Multiple(() =>
        {
            Assert.That(item.IsOnline, Is.False,
                "Redis respondeu e o agente não está lá: o status persistido Online é defasado.");
            Assert.That(item.Status, Is.EqualTo("Offline"));
        });
    }

    /// <summary>
    /// (c) Redis indisponível e agente com status persistido Online: NÃO houve resposta
    /// confiável, então vale a retaguarda — não podemos pintar a frota de Offline.
    /// </summary>
    [Test]
    public async Task RedisIndisponivelComStatusPersistidoOnline_ApareceOnline()
    {
        var agentId = Guid.NewGuid();
        // Exceção em toda leitura: nem o conjunto vazio pode ser interpretado como
        // "ninguém online".
        var cache = new UnavailableHeartbeatCache();

        // IsConnected = false: é este flag — e não o cache vazio — que diz ao handler
        // que o Redis está MUDO e que a resposta não é confiável.
        var item = await QuerySingleAsync(agentId, AgentStatus.Online, cache, redisConnected: false);

        Assert.Multiple(() =>
        {
            Assert.That(item.IsOnline, Is.True,
                "Sem resposta do Redis, a lista cai para o status persistido do banco.");
            Assert.That(item.Status, Is.EqualTo("Online"));
        });
    }

    /// <summary>
    /// (c-bis) O MESMO Redis indisponível, mas com "vazio silencioso" (sem exceção),
    /// que é o que o HeartbeatCacheService real faz atrás de um IRedisService
    /// desconectado. O handler não pode confundir esse vazio com "ninguém online".
    /// </summary>
    [Test]
    public async Task RedisDesconectadoComVazioSilencioso_UsaStatusPersistido()
    {
        var agentId = Guid.NewGuid();
        var cache = new OnlineAgentsFakeHeartbeatCache { SimulateUnavailable = true };

        var item = await QuerySingleAsync(agentId, AgentStatus.Online, cache, redisConnected: false);

        Assert.That(item.IsOnline, Is.True,
            "Redis desconectado devolve vazio para todos — o conjunto vazio aqui não é prova de frota offline.");
    }

    /// <summary>
    /// (d) Redis indisponível e agente com status persistido Offline: continua Offline
    /// (a retaguarda não inventa online).
    /// </summary>
    [Test]
    public async Task RedisIndisponivelComStatusPersistidoOffline_ApareceOffline()
    {
        var agentId = Guid.NewGuid();
        var cache = new UnavailableHeartbeatCache();

        var item = await QuerySingleAsync(agentId, AgentStatus.Offline, cache, redisConnected: false);

        Assert.That(item.IsOnline, Is.False);
    }

    /// <summary>
    /// (e) O campo Status do DTO continua sendo exatamente "Online"/"Offline" — o
    /// vocabulário do enum persistido que o site consome —, agora como veredito AO VIVO.
    /// Nunca string vazia, nunca o número do enum.
    /// </summary>
    [Test]
    public async Task CampoStatusSempreSaiComoOnlineOuOffline()
    {
        var onlineId = Guid.NewGuid();
        var offlineId = Guid.NewGuid();

        // Redis confiável: um agente dentro e outro fora do cache, com o banco
        // afirmando o CONTRÁRIO nos dois casos — o payload tem de seguir o Redis.
        var cache = new OnlineAgentsFakeHeartbeatCache(onlineId);

        var primeiro = await QuerySingleAsync(onlineId, AgentStatus.Offline, cache);
        var segundo = await QuerySingleAsync(offlineId, AgentStatus.Online, cache);

        Assert.Multiple(() =>
        {
            Assert.That(primeiro.Status, Is.EqualTo("Online"));
            Assert.That(primeiro.IsOnline, Is.True);
            Assert.That(segundo.Status, Is.EqualTo("Offline"));
            Assert.That(segundo.IsOnline, Is.False);
        });
    }

    /// <summary>
    /// O valor do DTO é sempre o NOME do enum ("Online"/"Offline"), o vocabulário que
    /// o site consome — o mapper normaliza um row sem status conhecido para "Offline",
    /// nunca para string vazia ou "0".
    /// </summary>
    [Test]
    public void Mapper_NormalizaRowSemStatusParaOffline()
    {
        var row = new PatchPendingAgentRow
        {
            AgentId = Guid.NewGuid(),
            Hostname = "HOST-SEM-STATUS",
            Status = null
        };

        var dto = PatchDtoMapper.ToDto(row, isOnline: false);

        Assert.Multiple(() =>
        {
            Assert.That(dto.Status, Is.EqualTo("Offline"));
            Assert.That(dto.IsOnline, Is.False);
            Assert.That(dto.Hostname, Is.EqualTo("HOST-SEM-STATUS"));
        });
    }

    /// <summary>
    /// Custo: a chave INDIVIDUAL é lida uma vez por agente da página — nunca a
    /// varredura por prefixo, que em produção não enxerga ninguém.
    /// </summary>
    [Test]
    public async Task PaginaInteira_LeAChaveIndividualDeCadaAgente()
    {
        var (db, _, agentAId, agentBId) = Seed();
        db.AgentWindowsUpdates.Add(Pending(agentAId));
        db.AgentWindowsUpdates.Add(Pending(agentBId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentAId));
        db.AgentWindowsUpdateSnapshots.Add(Snapshot(agentBId));
        await db.SaveChangesAsync();

        var cache = new OnlineAgentsFakeHeartbeatCache(agentAId, agentBId);
        var handler = new GetPatchPendingQueryHandler(
            new PatchRepository(db), cache, new FakeRedisService(isConnected: true));
        var result = await handler.Handle(
            new GetPatchPendingQuery(PatchScope.Global, null, null, 25, null, true, []), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.Value!.Items.All(item => item.IsOnline), Is.True);
            Assert.That(cache.KeyReadCount, Is.EqualTo(2));
        });
    }
}
