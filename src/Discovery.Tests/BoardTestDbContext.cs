using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Contexto InMemory reduzido compartilhado pelos testes do Patch Compliance
/// Dashboard e do Service Board (mesmo padrão de PatchRepositoryInventoryTests).
/// </summary>
internal sealed class BoardTestDbContext(DbContextOptions<DiscoveryDbContext> options)
    : DiscoveryDbContext(options)
{
    public static DiscoveryDbContext NewContext() =>
        new BoardTestDbContext(new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("service-board-" + Guid.NewGuid().ToString("N"))
            .Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var allowed = new HashSet<Type>
        {
            typeof(Client),
            typeof(Site),
            typeof(Agent),
            typeof(AgentWindowsUpdate),
            typeof(AgentWindowsUpdateSnapshot),
            typeof(Ticket),
            typeof(AutomationTaskDefinition),
            typeof(AutomationExecutionReport),
            typeof(AgentAlertDefinition)
        };

        foreach (var entityType in typeof(Client).Assembly.GetTypes()
                     .Where(type => type.IsClass && type.Namespace is not null &&
                                    type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                     .Where(type => !allowed.Contains(type)))
        {
            modelBuilder.Ignore(entityType);
        }

        modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
        modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
        modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));
        modelBuilder.Entity<AgentWindowsUpdate>(entity => entity.HasKey(item => item.Id));
        modelBuilder.Entity<AgentWindowsUpdateSnapshot>(entity => entity.HasKey(item => item.AgentId));
        modelBuilder.Entity<Ticket>(entity => entity.HasKey(item => item.Id));
        modelBuilder.Entity<AutomationTaskDefinition>(entity => entity.HasKey(item => item.Id));
        modelBuilder.Entity<AutomationExecutionReport>(entity => entity.HasKey(item => item.Id));
        modelBuilder.Entity<AgentAlertDefinition>(entity => entity.HasKey(item => item.Id));
    }
}
