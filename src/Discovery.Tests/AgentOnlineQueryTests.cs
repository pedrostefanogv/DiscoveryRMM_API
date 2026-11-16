using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
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
