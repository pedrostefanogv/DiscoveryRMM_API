using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.Patches;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class AppCatalogIngestionTests
{
    private static DiscoveryDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("app-catalog-ingest-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new CatalogIngestTestDbContext(options);
    }

    private sealed class CatalogIngestTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
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

    private static string RealManifest()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "Google.Chrome.installer.yaml");
        return File.ReadAllText(path);
    }

    [Test]
    public async Task IngestCreatesPendingEntryFromRealManifest()
    {
        var db = NewContext();
        var handler = new IngestWingetManifestCommandHandler(new AppCatalogRepository(db));

        var result = await handler.Handle(
            new IngestWingetManifestCommand(RealManifest(), "curador@example"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value;
        Assert.That(dto.Source, Is.EqualTo("winget"));
        Assert.That(dto.PackageId, Is.EqualTo("Google.Chrome"));
        Assert.That(dto.Status, Is.EqualTo("pending"), "ingestão não ativa sozinha (curadoria humana)");
        Assert.That(dto.Sha256, Has.Length.EqualTo(64));
        Assert.That(dto.InstallerUrl, Does.StartWith("https://"));
        Assert.That(dto.ProductCode, Is.Not.Null);
        Assert.That(await db.AppCatalogEntries.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task IngestRejectsManifestWithoutInstallers()
    {
        var db = NewContext();
        var handler = new IngestWingetManifestCommandHandler(new AppCatalogRepository(db));

        var result = await handler.Handle(
            new IngestWingetManifestCommand("PackageIdentifier: Vendor.App\nPackageVersion: 1.0.0\n"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(await db.AppCatalogEntries.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task ReIngestionDoesNotDowngradeCuratedEntry()
    {
        var db = NewContext();
        var repo = new AppCatalogRepository(db);
        var handler = new IngestWingetManifestCommandHandler(repo);

        // 1) ingestão cria pending
        var ingested = await handler.Handle(
            new IngestWingetManifestCommand(RealManifest(), "curador@example"), CancellationToken.None);
        Assert.That(ingested.Value.Status, Is.EqualTo("pending"));

        // 2) curador ativa a versão (upsert explícito)
        var active = await repo.UpsertAsync(new AppCatalogEntry
        {
            Source = "winget",
            PackageId = "Google.Chrome",
            Name = "Google Chrome",
            Version = "156.0.8078.48",
            InstallerUrl = "https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi",
            Sha256 = new string('a', 64),
            Status = "active",
            CuratedBy = "curador@example"
        });

        // 3) manifesto é re-ingerido: não pode rebaixar para pending
        await handler.Handle(new IngestWingetManifestCommand(RealManifest(), "pipeline"), CancellationToken.None);

        var reloaded = await repo.GetByIdAsync(active.Id);
        Assert.That(reloaded!.Status, Is.EqualTo("active"),
            "re-ingestão não pode desativar versão já curada");
    }
}
