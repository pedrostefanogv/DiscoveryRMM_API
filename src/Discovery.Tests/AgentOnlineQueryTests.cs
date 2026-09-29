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
