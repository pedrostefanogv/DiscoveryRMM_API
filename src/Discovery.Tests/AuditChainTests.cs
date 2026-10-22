using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Hash chain à prova de edição (M241): encadeamento correto, verificação
/// detecta edição e export das entradas encadeadas.
/// </summary>
[TestFixture]
public class AuditChainTests
{
    // ── Matemática da chain (pura) ─────────────────────────────────────────

    [Test]
    public void ComputeEntryHash_IsSha256OfPrevPlusPayload()
    {
        var payload = "payload-hash-base";
        var prev = new string('a', 64);

        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{prev}|{payload}"))).ToLowerInvariant();

        Assert.That(AuditChainService.ComputeEntryHash(prev, "configuration_audits", payload), Is.EqualTo(expected));
    }

    [Test]
    public void ComputeEntryHash_EmptyPrevUsesScopeAsGenesis()
    {
        var first = AuditChainService.ComputeEntryHash(null, "configuration_audits", "p1");
        var genesis = AuditChainService.ComputeEntryHash("configuration_audits", "configuration_audits", "p1");
        Assert.That(first, Is.EqualTo(genesis));
    }

    [Test]
    public void Verifier_AcceptsCorrectChain()
    {
        var p1 = "hash-p1";
        var p2 = "hash-p2";
        var e1 = AuditChainService.ComputeEntryHash(null, "configuration_audits", p1);
        var e2 = AuditChainService.ComputeEntryHash(e1, "configuration_audits", p2);

        var result = AuditChainVerifier.Verify("configuration_audits",
        [
            new AuditChainRow(Guid.NewGuid(), DateTime.UtcNow, null, e1, p1),
            new AuditChainRow(Guid.NewGuid(), DateTime.UtcNow, e1, e2, p2)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.EntriesChecked, Is.EqualTo(2));
            Assert.That(result.FirstBrokenId, Is.Null);
        });
    }

    [Test]
    public void Verifier_DetectsEditedEntryHash()
    {
        var p1 = "hash-p1";
        var p2 = "hash-p2";
        var e1 = AuditChainService.ComputeEntryHash(null, "configuration_audits", p1);
        var e2 = AuditChainService.ComputeEntryHash(e1, "configuration_audits", p2);
        var tampered = new Guid("11111111-1111-1111-1111-111111111111");

        var result = AuditChainVerifier.Verify("configuration_audits",
        [
            new AuditChainRow(Guid.NewGuid(), DateTime.UtcNow, null, e1, p1),
            new AuditChainRow(tampered, DateTime.UtcNow, e1, "0" + e2[1..], p2) // mutado
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.FirstBrokenId, Is.EqualTo(tampered));
            Assert.That(result.BrokenAt, Is.Not.Null);
        });
    }

    [Test]
    public void Verifier_IgnoresLegacyEntriesWithoutHash()
    {
        var p1 = "hash-p1";
        var e1 = AuditChainService.ComputeEntryHash(null, "configuration_audits", p1);

        var result = AuditChainVerifier.Verify("configuration_audits",
        [
            new AuditChainRow(Guid.NewGuid(), DateTime.UtcNow, null, null, "legado"),
            new AuditChainRow(Guid.NewGuid(), DateTime.UtcNow, null, e1, p1)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.EntriesChecked, Is.EqualTo(1));
        });
    }

    // ── Hook de gravação (repositório + serviço, InMemory) ─────────────────

    [Test]
    public async Task RepositoryHook_ChainsConsecutiveEntries()
    {
        await using var db = NewDb();
        var chain = new AuditChainService(db, NullLogger<AuditChainService>.Instance);
        var repo = new ConfigurationAuditRepository(db, chain, NullLogger<ConfigurationAuditRepository>.Instance);

        var first = NewAudit();
        var second = NewAudit();
        await repo.CreateAsync(first);
        await repo.CreateAsync(second);

        Assert.Multiple(() =>
        {
            Assert.That(first.EntryHash, Is.Not.Null);
            Assert.That(second.PrevHash, Is.EqualTo(first.EntryHash), "segunda entrada encadeia com a head da primeira");
        });

        // Verificação ponta a ponta recomputando o payload persistido.
        var rows = await db.ConfigurationAudits.AsNoTracking()
            .OrderBy(a => a.ChangedAt).ThenBy(a => a.Id).ToListAsync();
        var chainRows = rows.Select(a => new AuditChainRow(
            a.Id, a.ChangedAt, a.PrevHash, a.EntryHash,
            AuditChainService.ComputeConfigurationPayloadHash(a))).ToList();

        Assert.That(AuditChainVerifier.Verify("configuration_audits", chainRows).Ok, Is.True);
    }

    [Test]
    public async Task ScriptAuditHook_ChainsIndependentlyOfConfigurationScope()
    {
        await using var db = NewDb();
        var chain = new AuditChainService(db, NullLogger<AuditChainService>.Instance);
        var scriptRepo = new AutomationScriptAuditRepository(db, chain, NullLogger<AutomationScriptAuditRepository>.Instance);
        var configRepo = new ConfigurationAuditRepository(db, chain, NullLogger<ConfigurationAuditRepository>.Instance);

        var scriptAudit = new AutomationScriptAudit { ScriptId = Guid.NewGuid(), ChangeType = Discovery.Core.Enums.AutomationScriptChangeType.Created };
        await scriptRepo.CreateAsync(scriptAudit);
        var configAudit = NewAudit();
        await configRepo.CreateAsync(configAudit);

        // Escopos por tabela: o gênese de um não depende do outro, mas ambos
        // partem de null (chain vazia) — a head de cada um é independente.
        Assert.Multiple(() =>
        {
            Assert.That(scriptAudit.EntryHash, Is.Not.Null);
            Assert.That(configAudit.PrevHash, Is.Null, "escopo configuration_audits ainda está vazio (chains são por tabela)");
        });
    }

    [Test]
    public async Task AppendAsync_FailsOpenOnBrokenHeadQuery()
    {
        await using var db = NewDb();
        // Serviço com contexto "quebrado" (object disposed) força exceção no head query.
        var broken = new AuditChainService(db, NullLogger<AuditChainService>.Instance);
        await db.Database.EnsureCreatedAsync();
        await db.DisposeAsync();

        var (prev, entry) = await broken.AppendAsync("configuration_audits", "payload");

        Assert.Multiple(() =>
        {
            Assert.That(prev, Is.Null);
            Assert.That(entry, Is.Null);
        });
    }

    // ── Export ──────────────────────────────────────────────────────────────

    [Test]
    public void Export_CsvEscapesAndIncludesHashes()
    {
        var id = Guid.NewGuid();
        var csv = Discovery.Infrastructure.Cqrs.ConfigurationAudit.ExportAuditChainQueryHandler.ToCsv(
        [
            new AuditChainRow(id, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "prev", "entry", "payload")
        ]);

        Assert.That(csv, Does.Contain("id,changed_at,prev_hash,entry_hash,payload_hash"));
        Assert.That(csv, Does.Contain(id.ToString()));
        Assert.That(csv, Does.Contain("entry"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ConfigurationAudit NewAudit() => new()
    {
        EntityType = Discovery.Core.Enums.ConfigurationEntityType.Client,
        EntityId = Guid.NewGuid(),
        FieldName = "field-" + Guid.NewGuid().ToString("N")[..6],
        OldValue = "old",
        NewValue = "new"
    };

    private static AuditChainTestDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"audit-chain-{Guid.NewGuid():N}")
            .Options;
        return new AuditChainTestDbContext(options);
    }

    /// <summary>Contexto restrito a ConfigurationAudit (InMemory não suporta o modelo completo).</summary>
    private sealed class AuditChainTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(Discovery.Core.Entities.Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(ConfigurationAudit) && type != typeof(AutomationScriptAudit)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<ConfigurationAudit>(entity =>
            {
                entity.HasKey(a => a.Id);
                entity.Property(a => a.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<AutomationScriptAudit>(entity =>
            {
                entity.HasKey(a => a.Id);
                entity.Property(a => a.Id).ValueGeneratedNever();
            });
        }
    }
}