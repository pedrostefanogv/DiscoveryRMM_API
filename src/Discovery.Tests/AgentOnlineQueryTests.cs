using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// GetOnlineAsync é usado pela expiração de heartbeat e pela reentrega de
/// comandos. Um agente na lixeira continua com Status=Online até o heartbeat
/// expirar — se ele entrar nessa lista, volta a receber comandos depois de
/// excluído.
/// </summary>
[TestFixture]
public class AgentOnlineQueryTests
{
    [Test]
    public async Task GetOnlineAsync_ExcludesTrashedAndOfflineAgents()
    {
        await using var db = NewDb();

        var online = NewAgent(AgentStatus.Online, deleted: false);
        var trashed = NewAgent(AgentStatus.Online, deleted: true);
        var offline = NewAgent(AgentStatus.Offline, deleted: false);
        db.Agents.AddRange(online, trashed, offline);
        await db.SaveChangesAsync();

        var result = await new AgentRepository(db).GetOnlineAsync();

        Assert.That(result.Select(agent => agent.Id), Is.EqualTo(new[] { online.Id }));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Regressão: o agente na LIXEIRA precisa entrar na reentrega.
    //
    // Este é o teste que faltava. Os testes de reentrega existentes exercitam
    // apenas funções puras (SelectRedeliveryAgentIds / IsRedeliveryAllowedForAgent)
    // com listas montadas à mão, então NUNCA tocavam a consulta real do
    // repositório. O filtro global de soft delete (HasQueryFilter
    // DeletedAt == null) escondia o agente excluído da consulta e o
    // DecommissionAgent ficava Pending para sempre — o PC nunca era
    // desinstalado.
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetOnlineIncludingTrashedAsync_IncludesTrashedAgentStillOnline()
    {
        await using var db = NewDb();

        var online = NewAgent(AgentStatus.Online, deleted: false);
        var trashed = NewAgent(AgentStatus.Online, deleted: true);
        var offline = NewAgent(AgentStatus.Offline, deleted: false);
        db.Agents.AddRange(online, trashed, offline);
        await db.SaveChangesAsync();

        var result = await new AgentRepository(db).GetOnlineIncludingTrashedAsync();

        Assert.That(
            result.Select(agent => agent.Id).OrderBy(id => id),
            Is.EqualTo(new[] { online.Id, trashed.Id }.OrderBy(id => id)),
            "o agente na lixeira ainda ONLINE na máquina precisa ser alcançável para receber o DecommissionAgent");
        Assert.That(result.Any(agent => agent.Id == offline.Id), Is.False, "agente offline continua fora");
    }

    [Test]
    public async Task GetByIdForDecommissionAsync_SeesTrashedAgent()
    {
        await using var db = NewDb();

        var trashed = NewAgent(AgentStatus.Online, deleted: true);
        db.Agents.Add(trashed);
        await db.SaveChangesAsync();

        var repo = new AgentRepository(db);

        // O lookup de descomissionamento precisa enxergar o agente excluído:
        // é ele que a autorização de NATS consulta para não derrubar o heartbeat
        // de quem ainda está instalado na máquina.
        //
        // (Não se afirma aqui que GetByIdAsync devolve null: o provider InMemory
        // NÃO aplica o filtro global HasQueryFilter, então essa expectativa só
        // vale contra o Postgres real — assumi-la aqui daria um teste que passa
        // pelo motivo errado.)
        var found = await repo.GetByIdForDecommissionAsync(trashed.Id);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.DeletedAt, Is.Not.Null, "o lookup precisa trazer o DeletedAt preenchido");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Regressão: o SUBJECT do comando precisa ser montável para um agente na
    // lixeira.
    //
    // Este era o elo que faltava depois da correção de 2026-11-16. A reentrega
    // já alcançava o agente da lixeira (GetOnlineIncludingTrashedAsync), mas o
    // envio em si morria em NatsAgentMessaging.BuildAgentSubjectAsync, que
    // resolvia o tenant com GetByIdAsync — filtrado pelo HasQueryFilter global
    // (DeletedAt == null). Resultado: "Agent not found" no exato momento de
    // mandar o DecommissionAgent, o dispatcher engolia a exceção (best-effort) e
    // o comando ficava Pending para sempre. O agente na máquina nunca recebia o
    // comando e a lixeira nunca era confirmada.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O TESTE que faltava — e que falha com o bug.
    ///
    /// Dirige o caminho REAL de resolução do subject (BuildAgentSubjectAsync)
    /// contra um repositório de verdade, com o filtro global de soft delete
    /// ATIVO (SQLite). Com o lookup filtrado (GetByIdAsync), este teste falha com
    /// "Agent not found" — exatamente o que acontecia em produção ao tentar
    /// enviar o DecommissionAgent para o agente que acabou de ir para a lixeira.
    /// </summary>
    [Test]
    public async Task BuildAgentSubject_TrashedAgent_ResolvesUnicastCommandSubject()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new SqliteSoftDeleteTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var trashed = NewAgent(AgentStatus.Online, deleted: true);
        trashed.SiteId = siteId;
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site", IsActive = true });
        db.Agents.Add(trashed);
        await db.SaveChangesAsync();

        var subject = await NatsAgentMessaging.BuildAgentSubjectAsync(
            new AgentRepository(db),
            new SiteRepository(db),
            trashed.Id,
            "command");

        Assert.That(
            subject,
            Is.EqualTo($"tenant.{clientId}.site.{siteId}.agent.{trashed.Id}.command"),
            "o agente na lixeira precisa de um subject válido: é por ele que o comando de desinstalação chega");
    }

    [Test]
    public void ResolveAgentSubject_TrashedAgent_ProducesUnicastCommandSubject()
    {
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var trashed = new Agent
        {
            Id = agentId,
            SiteId = siteId,
            Hostname = "PC-EXCLUIDO",
            Status = AgentStatus.Online,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var site = new Site { Id = siteId, ClientId = clientId, Name = "Site", IsActive = true };

        var subject = NatsAgentMessaging.ResolveAgentSubject(trashed, site, agentId, "command");

        Assert.That(
            subject,
            Is.EqualTo($"tenant.{clientId}.site.{siteId}.agent.{agentId}.command"),
            "o agente na lixeira precisa de um subject válido: é por ele que o comando de desinstalação chega");
    }

    /// <summary>
    /// Agente na lixeira não deve ser anunciado como peer P2P: ele está sendo
    /// desinstalado. O heartbeat continua aceito (é o que o mantém alcançável
    /// para o DecommissionAgent) — o que se suprime é só o anúncio.
    /// </summary>
    [Test]
    public void ShouldPublishPeerOnline_SuppressesTrashedAgent()
    {
        Assert.That(
            NatsAgentMessaging.ShouldPublishPeerOnline(wasTransition: true, peerId: "12D3Koo...", isTrashed: true),
            Is.False,
            "agente em desinstalação não deve ser anunciado como peer do tenant");

        Assert.That(
            NatsAgentMessaging.ShouldPublishPeerOnline(wasTransition: true, peerId: "12D3Koo...", isTrashed: false),
            Is.True,
            "agente ativo na transição Offline→Online deve ser anunciado");
    }

    [Test]
    public void ShouldPublishPeerOnline_RequiresTransitionAndPeerId()
    {
        Assert.That(NatsAgentMessaging.ShouldPublishPeerOnline(false, "peer", false), Is.False, "sem transição não anuncia");
        Assert.That(NatsAgentMessaging.ShouldPublishPeerOnline(true, null, false), Is.False, "sem PeerId não anuncia");
        Assert.That(NatsAgentMessaging.ShouldPublishPeerOnline(true, "   ", false), Is.False, "PeerId vazio não anuncia");
    }

    [Test]
    public void ResolveAgentSubject_MissingAgent_Throws()
    {
        var site = new Site { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Name = "Site", IsActive = true };

        Assert.That(
            () => NatsAgentMessaging.ResolveAgentSubject(null, site, Guid.NewGuid(), "command"),
            Throws.InvalidOperationException);
    }

    [Test]
    public void ResolveAgentSubject_MissingSite_Throws()
    {
        var agent = NewAgent(AgentStatus.Online, deleted: true);

        Assert.That(
            () => NatsAgentMessaging.ResolveAgentSubject(agent, null, agent.Id, "command"),
            Throws.InvalidOperationException);
    }

    /// <summary>
    /// PROVA da causa raiz (com o filtro GLOBAL ativo, via SQLite).
    ///
    /// O teste acima usa InMemory, que NÃO aplica HasQueryFilter — por isso ele
    /// passou enquanto o bug existia. Aqui o provider é SQLite, que aplica o
    /// filtro de verdade: GetByIdAsync (usado pelo dispatch para montar o subject)
    /// devolve null para o agente na lixeira, e é exatamente essa diferença que
    /// fazia o envio do DecommissionAgent morrer com "Agent not found" enquanto
    /// o lookup de descomissionamento continuava enxergando o agente.
    /// </summary>
    [Test]
    public async Task Sqlite_QueryFilter_HidesTrashedAgentFromGetByIdAsync()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new SqliteSoftDeleteTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var trashed = NewAgent(AgentStatus.Online, deleted: true);
        db.Agents.Add(trashed);
        await db.SaveChangesAsync();

        var repo = new AgentRepository(db);

        Assert.That(
            await repo.GetByIdAsync(trashed.Id),
            Is.Null,
            "o filtro global esconde o agente na lixeira — era isso que quebrava o uso de GetByIdAsync no dispatch");

        Assert.That(
            await repo.GetByIdForDecommissionAsync(trashed.Id),
            Is.Not.Null,
            "o lookups de descomissionamento PRECISA enxergar o agente na lixeira");
    }

    private static Agent NewAgent(AgentStatus status, bool deleted) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Hostname = "PC-1",
        Status = status,
        DeletedAt = deleted ? DateTime.UtcNow : null,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentOnlineTestDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"agent-online-{Guid.NewGuid():N}")
            .Options;

        return new AgentOnlineTestDbContext(options);
    }

    /// <summary>
    /// Contexto SQLite restrito a Agent/Site. Diferente do InMemory, o SQLite
    /// aplica o HasQueryFilter global (DeletedAt == null) — é o que permite
    /// provar o comportamento real do soft delete no dispatch.
    /// </summary>
    private sealed class SqliteSoftDeleteTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(Agent) && type != typeof(Site)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Agent>(entity => entity.HasKey(agent => agent.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(site => site.Id));

            // Reproduz o filtro global do contexto real (DiscoveryDbContext.CoreEntities.cs).
            modelBuilder.Entity<Agent>().HasQueryFilter(agent => agent.DeletedAt == null);
        }
    }

    /// <summary>Contexto restrito a Agent/Site (InMemory não suporta o modelo completo).</summary>
    private sealed class AgentOnlineTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(Agent) && type != typeof(Site)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Agent>(entity => entity.HasKey(agent => agent.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(site => site.Id));
        }
    }
}
