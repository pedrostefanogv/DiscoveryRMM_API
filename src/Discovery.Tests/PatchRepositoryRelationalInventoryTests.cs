using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Caminho RELACIONAL do upload de inventário (Postgres em produção).
///
/// Os demais testes usam o provider InMemory, que não executa
/// <c>ExecuteUpdateAsync</c> — justamente a chamada que podia deixar o change
/// tracker dessincronizado e fazer o inventário inteiro sumir (is_present=false
/// no banco, true na memória). Estes testes rodam contra SQLite (relacional, em
/// memória) e leem o estado do BANCO, não do tracker.
/// </summary>
[TestFixture]
public class PatchRepositoryRelationalInventoryTests
{
    private SqliteConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private DiscoveryDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(_connection)
            .Options;
        var db = new RelationalTestDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>
    /// Semeia cliente/site/agente na MESMA conexão SQLite do teste (uma conexão
    /// aberta, um banco em memória).
    /// </summary>
    private (DiscoveryDbContext Db, Guid AgentId) Seed()
    {
        var db = NewContext();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        db.Clients.Add(new Client { Id = clientId, Name = "Cliente" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site" });
        db.Agents.Add(new Agent { Id = agentId, SiteId = siteId, Hostname = "HOST" });
        db.SaveChanges();
        return (db, agentId);
    }

    private static AgentWindowsUpdateEntry Entry(string updateKey, bool installed, string kb) => new()
    {
        UpdateKey = updateKey,
        KbIds = "[\"" + kb + "\"]",
        Title = "Update " + kb,
        IsInstalled = installed,
        IsPending = !installed,
        Source = "wua",
        InstalledAt = installed ? new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc) : null
    };

    [Test]
    public async Task TwoConsecutiveUploadsKeepRowsPresentInDatabase()
    {
        var (db, agentId) = Seed();
        var repo = new PatchRepository(db);
        var entries = new[]
        {
            Entry("guid:11111111-1111-1111-1111-111111111111:200", installed: false, kb: "KB5127000"),
            Entry("kb:KB5127690", installed: true, kb: "KB5127690")
        };

        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, new PatchScanMeta { Status = "ok" }, null, entries);

        // Segundo upload com o MESMO payload: aqui o bug do tracker aparecia
        // (find-all antes do ExecuteUpdate + is_present não regravado).
        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, new PatchScanMeta { Status = "ok" }, null, entries);

        db.ChangeTracker.Clear();
        var rows = await db.AgentWindowsUpdates.AsNoTracking()
            .Where(row => row.AgentId == agentId)
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Count(row => row.IsPresent && row.IsPending), Is.EqualTo(1), "pendente presente no banco");
        Assert.That(rows.Count(row => row.IsPresent && row.IsInstalled), Is.EqualTo(1), "instalada presente no banco");

        var snapshot = await db.AgentWindowsUpdateSnapshots.AsNoTracking()
            .SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingCount, Is.EqualTo(1));
        Assert.That(snapshot.InstalledCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RemovedPendingIsSupersededAndCountsFollowRows()
    {
        var (db, agentId) = Seed();
        var repo = new PatchRepository(db);

        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, new PatchScanMeta { Status = "ok" }, null,
        [
            Entry("guid:11111111-1111-1111-1111-111111111111:200", installed: false, kb: "KB5127000"),
            Entry("kb:KB5127690", installed: true, kb: "KB5127690")
        ]);

        // Scan completo sem a pendente: vira Superseded e sai da contagem.
        await repo.ReplaceInventoryAsync(agentId, DateTime.UtcNow, new PatchScanMeta { Status = "ok" }, null,
            [Entry("kb:KB5127690", installed: true, kb: "KB5127690")]);

        db.ChangeTracker.Clear();
        var rows = await db.AgentWindowsUpdates.AsNoTracking()
            .Where(row => row.AgentId == agentId)
            .ToListAsync();
        Assert.That(rows.Single(row => !row.IsInstalled).Status, Is.EqualTo(PatchUpdateStatus.Superseded));

        var snapshot = await db.AgentWindowsUpdateSnapshots.AsNoTracking()
            .SingleAsync(item => item.AgentId == agentId);
        Assert.That(snapshot.PendingCount, Is.EqualTo(0));
        Assert.That(snapshot.InstalledCount, Is.EqualTo(1));
    }

    /// <summary>Modelo reduzido: só as entidades usadas, sem pgvector/jsonb do Postgres.</summary>
    private sealed class RelationalTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(AgentWindowsUpdate),
                typeof(AgentWindowsUpdateSnapshot),
                typeof(PatchApproval),
                typeof(PatchPolicy)
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
            modelBuilder.Entity<PatchApproval>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<PatchPolicy>(entity => entity.HasKey(item => item.Id));

            // Colunas jsonb não existem no SQLite: usa TEXT só neste teste.
            entityTypeJsonColumns(modelBuilder);
        }

        private static void entityTypeJsonColumns(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AgentWindowsUpdate>()
                .Property(row => row.KbIds).HasColumnType("TEXT");
            modelBuilder.Entity<AgentWindowsUpdate>()
                .Property(row => row.Categories).HasColumnType("TEXT");
            modelBuilder.Entity<AgentWindowsUpdate>()
                .Property(row => row.Raw).HasColumnType("TEXT");
            modelBuilder.Entity<AgentWindowsUpdateSnapshot>()
                .Property(row => row.PendingRebootReasons).HasColumnType("TEXT");
            modelBuilder.Entity<AgentWindowsUpdateSnapshot>()
                .Property(row => row.SkippedReasons).HasColumnType("TEXT");
        }
    }
}
