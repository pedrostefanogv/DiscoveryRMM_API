using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class AppCatalogRepositoryTests
{
    private static DiscoveryDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("app-catalog-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new CatalogTestDbContext(options);
    }

    private sealed class CatalogTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Mesmo padrão dos outros fixtures: ignora TODAS as entidades de
            // Core, exceto a que este teste usa (o InMemory não lida com a
            // coluna pgvector de KnowledgeArticleChunk).
            var allowed = new HashSet<Type> { typeof(AppCatalogEntry) };
            foreach (var entityType in typeof(AppCatalogEntry).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AppCatalogEntry>(entity => entity.HasKey(entry => entry.Id));
        }
    }

    private static AppCatalogEntry Entry(string version = "1.0.0", string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") => new()
    {
        Source = "winget",
        PackageId = "Google.Chrome",
        Name = "Google Chrome",
        Version = version,
        InstallerUrl = "https://dl.google.com/chrome/install/1.0.0.msi",
        Sha256 = sha,
        InstallerType = "msi",
        SilentArgs = "/qn",
        ProductCode = "{ABC}",
        CuratedBy = "tester"
    };

    [Test]
    public async Task UpsertCreatesAndUpdatesSameVersion()
    {
        var db = NewContext();
        var repo = new AppCatalogRepository(db);

        var first = await repo.UpsertAsync(Entry());
        Assert.That(first.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(first.Status, Is.EqualTo("active"));

        var updated = Entry();
        updated.Name = "Google Chrome (atualizado)";
        var second = await repo.UpsertAsync(updated);

        Assert.That(second.Id, Is.EqualTo(first.Id), "mesma versão atualiza a linha existente");
        Assert.That(second.Name, Is.EqualTo("Google Chrome (atualizado)"));
        Assert.That(await db.AppCatalogEntries.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UpsertRejectsInvalidIntegrityData()
    {
        var repo = new AppCatalogRepository(NewContext());

        var badHash = Entry(sha: "xyz");
        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertAsync(badHash));

        var badUrl = Entry();
        badUrl.InstallerUrl = "http://inseguro.example/app.msi";
        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertAsync(badUrl));

        var badSource = Entry();
        badSource.Source = "scoop";
        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertAsync(badSource));
    }

    [Test]
    public async Task RevokeRemovesFromActiveKeys()
    {
        var db = NewContext();
        var repo = new AppCatalogRepository(db);
        var entry = await repo.UpsertAsync(Entry());

        Assert.That(await repo.GetActiveKeysAsync(), Does.Contain("winget:google.chrome"));

        Assert.That(await repo.RevokeAsync(entry.Id, "analista"), Is.True);
        var revoked = await repo.GetByIdAsync(entry.Id);
        Assert.That(revoked!.Status, Is.EqualTo("revoked"));
        Assert.That(revoked.RevokedBy, Is.EqualTo("analista"));
        Assert.That(revoked.RevokedAt, Is.Not.Null);

        Assert.That(await repo.GetActiveKeysAsync(), Is.Empty);
    }

    [Test]
    public async Task RecurationReactivatesRevokedVersion()
    {
        var db = NewContext();
        var repo = new AppCatalogRepository(db);
        var entry = await repo.UpsertAsync(Entry());
        await repo.RevokeAsync(entry.Id, "analista");

        await repo.UpsertAsync(Entry());

        var reactivated = await repo.GetByIdAsync(entry.Id);
        Assert.That(reactivated!.Status, Is.EqualTo("active"));
        Assert.That(reactivated.RevokedAt, Is.Null);
    }

    [Test]
    public async Task ListFiltersBySourceStatusAndSearch()
    {
        var db = NewContext();
        var repo = new AppCatalogRepository(db);
        await repo.UpsertAsync(Entry("1.0.0"));
        await repo.UpsertAsync(Entry("2.0.0"));

        Assert.That(await repo.ListAsync(null, null, "chrome", 10), Has.Count.EqualTo(2));
        Assert.That(await repo.ListAsync("msi", null, null, 10), Is.Empty);
        Assert.That(await repo.ListAsync("winget", "active", null, 10), Has.Count.EqualTo(2));
    }
}
