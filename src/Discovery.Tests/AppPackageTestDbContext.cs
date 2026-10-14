using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Contexto InMemory restrito a <see cref="AppPackage"/> para os testes dos campos
/// novos da loja (M223). O modelo completo do DiscoveryDbContext não sobe no
/// provider InMemory (jsonb/vector), então o mesmo padrão de
/// <c>AgentRepositoryUpdateTests</c> é aplicado aqui — e nenhum banco real é usado.
/// </summary>
internal sealed class AppPackageTestDbContext(DbContextOptions<DiscoveryDbContext> options)
    : DiscoveryDbContext(options)
{
    public static DiscoveryDbContext New()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"app-package-custom-{Guid.NewGuid():N}")
            .Options;
        return new AppPackageTestDbContext(options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entityType in typeof(AppPackage).Assembly.GetTypes()
                     .Where(type => type.IsClass && type.Namespace is not null
                         && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                     .Where(type => type != typeof(AppPackage)))
        {
            modelBuilder.Ignore(entityType);
        }

        modelBuilder.Entity<AppPackage>(entity => entity.HasKey(p => p.Id));
    }
}
