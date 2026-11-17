using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão do descomissionamento de agente OFFLINE.
///
/// O caminho "agente offline vai para a lixeira e depois volta online" depende de
/// a reentrega (PendingCommandRedeliveryService) enxergá-lo Online. A lista vem
/// de GetOnlineIncludingTrashedAsync, que lê agents.status. Quem grava esse
/// status é UpdateStatusAsync — e ele usava o DbSet FILTRADO, então o
/// HasQueryFilter global (DeletedAt == null) fazia o UPDATE não atingir o agente
/// na lixeira. Resultado: o agente voltava online, publicava heartbeat, mas
/// continuava Offline no banco e o DecommissionAgent nunca era entregue.
///
/// SQLite (e não InMemory) porque só o provider relacional aplica o filtro
/// global em ExecuteUpdateAsync — um teste com InMemory passaria pelo motivo
/// errado.
/// </summary>
[TestFixture]
public class AgentTrashedStatusUpdateTests
{
    [Test]
    public async Task UpdateStatusAsync_ReachesTrashedAgent()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new SqliteStatusTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var trashed = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            Hostname = "PC-EXCLUIDO",
            Status = AgentStatus.Offline,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Agents.Add(trashed);
        await db.SaveChangesAsync();

        await new AgentRepository(db).UpdateStatusAsync(trashed.Id, AgentStatus.Online, "192.168.0.10");

        var reloaded = await db.Agents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(agent => agent.Id == trashed.Id);

        Assert.That(
            reloaded.Status,
            Is.EqualTo(AgentStatus.Online),
            "o agente na lixeira que voltou a enviar heartbeat PRECISA ser marcado Online, senão a reentrega nunca o encontra e a máquina fica instalada");
    }

    /// <summary>
    /// Uma vez Online no banco, a consulta de reentrega tem de enxergá-lo — este
    /// é o elo que liga a correção acima ao comando de desinstalação.
    /// </summary>
    [Test]
    public async Task TrashedOnlineAgent_IsSelectedForRedelivery()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new SqliteStatusTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var trashed = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            Hostname = "PC-EXCLUIDO-ONLINE",
            Status = AgentStatus.Offline,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Agents.Add(trashed);
        await db.SaveChangesAsync();

        var repo = new AgentRepository(db);
        await repo.UpdateStatusAsync(trashed.Id, AgentStatus.Online, null);

        var candidates = await repo.GetOnlineIncludingTrashedAsync();

        Assert.That(
            candidates.Select(agent => agent.Id),
            Does.Contain(trashed.Id),
            "o agente na lixeira que voltou online precisa entrar na fila de reentrega do DecommissionAgent");
    }

    private sealed class SqliteStatusTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
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

            // Reproduz o filtro global do contexto real.
            modelBuilder.Entity<Agent>().HasQueryFilter(agent => agent.DeletedAt == null);
        }
    }
}
