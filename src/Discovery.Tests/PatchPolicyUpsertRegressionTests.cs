using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão do upsert de política de patches (WU-19/WU-20, D22).
///
/// Bug de campo: desmarcar "Aplicativos de terceiros" na tela de políticas
/// devolvia HTTP 200 mas <c>sources</c> continuava <c>["os","third_party"]</c>
/// no GET seguinte. Causa: <see cref="PatchRepository.UpsertPolicyAsync"/>
/// copiava só um SUBSET dos campos para a linha EXISTENTE — <c>Sources</c>,
/// <c>AllowThirdPartyApps</c>, <c>DeferralDays</c> e <c>DeadlineDays</c> eram
/// silenciosamente descartados no caminho de UPDATE (o caminho de INSERT
/// gravava a entidade inteira, por isso o bug só aparecia ao editar).
///
/// Os testes rodam contra SQLite relacional (mesmo padrão de
/// <c>PatchRepositoryRelationalInventoryTests</c>): o estado é lido do BANCO
/// depois de <c>ChangeTracker.Clear()</c>, então o teste não pode passar
/// apenas porque o change tracker manteve a entidade em memória.
/// </summary>
[TestFixture]
public class PatchPolicyUpsertRegressionTests
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
        var db = new PolicyTestDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>Modelo reduzido: só as entidades usadas, sem pgvector/jsonb do Postgres.</summary>
    private sealed class PolicyTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
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
            modelBuilder.Entity<PatchPolicy>(entity => entity.HasKey(item => item.Id));
        }
    }

    /// <summary>Política inicial: terceiros LIGADOS (sources os+third_party).</summary>
    private static PatchPolicy ThirdPartyEnabledPolicy() => new()
    {
        Id = Guid.NewGuid(),
        ScopeType = PatchApprovalScopeType.Global,
        ScopeId = null,
        EnforcementMode = PatchEnforcementMode.Monitor,
        AutoInstallEnabled = false,
        RebootPolicy = PatchRebootPolicy.Never,
        RebootAllowed = false,
        DeferralDays = 7,
        DeadlineDays = 14,
        Sources = PatchJson.SerializeStringArray([PatchSourcePolicy.Os, PatchSourcePolicy.ThirdParty]),
        AllowThirdPartyApps = true,
        MaxInstallsPerRun = 50,
        UpdatedBy = "seed",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    /// <summary>Estado do BANCO (não do change tracker) para o escopo global.</summary>
    private static async Task<PatchPolicy> ReadGlobalPolicyAsync(DiscoveryDbContext db)
    {
        db.ChangeTracker.Clear();
        return await db.PatchPolicies.AsNoTracking()
            .SingleAsync(policy => policy.ScopeType == PatchApprovalScopeType.Global);
    }

    [Test]
    public async Task UpdateOnExistingRowPersistsSourcesAllowThirdPartyDeferralAndDeadline()
    {
        var db = NewContext();
        var repo = new PatchRepository(db);

        // 1) INSERT: a linha passa a existir com terceiros ligados.
        var created = await repo.UpsertPolicyAsync(ThirdPartyEnabledPolicy());
        Assert.That(created.AllowThirdPartyApps, Is.True, "pré-condição: INSERT grava terceiros ligados");
        Assert.That(created.DeferralDays, Is.EqualTo(7), "pré-condição: INSERT grava deferral");

        // 2) UPDATE da MESMA linha: usuário desmarca "Aplicativos de terceiros".
        var incoming = ThirdPartyEnabledPolicy();
        incoming.Id = created.Id; // mesma linha: o upsert casa por (ScopeType, ScopeId)
        incoming.Sources = PatchJson.SerializeStringArray([PatchSourcePolicy.Os]);
        incoming.AllowThirdPartyApps = false;
        incoming.DeferralDays = 0;
        incoming.DeadlineDays = 0;

        var returned = await repo.UpsertPolicyAsync(incoming);

        // 3) Retorno do repositório reflete os valores NOVOS.
        Assert.That(returned.Sources, Is.EqualTo("[\"os\"]"),
            "retorno do upsert deve gravar sources somente com os");
        Assert.That(returned.AllowThirdPartyApps, Is.False,
            "retorno do upsert deve desligar AllowThirdPartyApps");
        Assert.That(returned.DeferralDays, Is.EqualTo(0), "retorno do upsert deve zerar DeferralDays");
        Assert.That(returned.DeadlineDays, Is.EqualTo(0), "retorno do upsert deve zerar DeadlineDays");

        // 4) O BANCO (não o tracker) reflete os valores NOVOS — é o que o GET lê.
        var persisted = await ReadGlobalPolicyAsync(db);
        Assert.That(persisted.Sources, Is.EqualTo("[\"os\"]"),
            "sources persistido deve ser [\"os\"] depois de desmarcar terceiros");
        Assert.That(PatchJson.ParseStringArray(persisted.Sources), Is.EquivalentTo(new[] { "os" }),
            "sources persistido não pode mais conter third_party");
        Assert.That(persisted.AllowThirdPartyApps, Is.False,
            "allowThirdPartyApps persistido deve ser false");
        Assert.That(persisted.DeferralDays, Is.EqualTo(0), "deferralDays persistido deve ser 0");
        Assert.That(persisted.DeadlineDays, Is.EqualTo(0), "deadlineDays persistido deve ser 0");

        // 5) Efeito de consentimento: o duplo consentimento cai com o update.
        Assert.That(PatchSourcePolicy.ThirdPartyConsented(persisted.Sources, persisted.AllowThirdPartyApps),
            Is.False, "sem consentimento após desmarcar terceiros");
    }

    [Test]
    public async Task UpdateOnExistingRowPersistsNewNonZeroDeferralAndDeadline()
    {
        var db = NewContext();
        var repo = new PatchRepository(db);

        var created = await repo.UpsertPolicyAsync(new PatchPolicy
        {
            Id = Guid.NewGuid(),
            ScopeType = PatchApprovalScopeType.Global,
            ScopeId = null,
            DeferralDays = 0,
            DeadlineDays = 0,
            Sources = PatchJson.SerializeStringArray([PatchSourcePolicy.Os]),
            AllowThirdPartyApps = false,
            UpdatedBy = "seed"
        });

        var incoming = new PatchPolicy
        {
            Id = created.Id,
            ScopeType = PatchApprovalScopeType.Global,
            ScopeId = null,
            DeferralDays = 21,
            DeadlineDays = 45,
            Sources = PatchJson.SerializeStringArray([PatchSourcePolicy.Os, PatchSourcePolicy.ThirdParty]),
            AllowThirdPartyApps = true,
            UpdatedBy = "editor"
        };

        await repo.UpsertPolicyAsync(incoming);

        var persisted = await ReadGlobalPolicyAsync(db);
        Assert.That(persisted.DeferralDays, Is.EqualTo(21), "deferralDays novo deve persistir");
        Assert.That(persisted.DeadlineDays, Is.EqualTo(45), "deadlineDays novo deve persistir");
        Assert.That(persisted.AllowThirdPartyApps, Is.True, "allowThirdPartyApps novo deve persistir");
        Assert.That(PatchJson.ParseStringArray(persisted.Sources),
            Is.EquivalentTo(new[] { "os", "third_party" }), "sources novo deve persistir");
        Assert.That(persisted.UpdatedBy, Is.EqualTo("editor"), "updatedBy segue sendo gravado");
    }

    [Test]
    public async Task UpdateOnExistingRowDoesNotCreateSecondRow()
    {
        var db = NewContext();
        var repo = new PatchRepository(db);

        var created = await repo.UpsertPolicyAsync(ThirdPartyEnabledPolicy());

        var incoming = ThirdPartyEnabledPolicy();
        incoming.Id = created.Id;
        incoming.Sources = PatchJson.SerializeStringArray([PatchSourcePolicy.Os]);
        incoming.AllowThirdPartyApps = false;

        await repo.UpsertPolicyAsync(incoming);

        db.ChangeTracker.Clear();
        var rows = await db.PatchPolicies.AsNoTracking().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1), "upsert por escopo deve atualizar a linha existente");
        Assert.That(rows[0].Id, Is.EqualTo(created.Id));
    }
}
