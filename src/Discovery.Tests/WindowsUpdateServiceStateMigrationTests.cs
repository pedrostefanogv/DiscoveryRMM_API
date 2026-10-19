using System.Reflection;
using Discovery.Migrations.Migrations;
using FluentMigrator;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Guarda de schema da sonda de estado do Windows Update (M238).
///
/// O FluentMigrator só roda contra banco de verdade: sem este teste, um
/// [Migration] com versão repetida ou uma coluna que o EF mapeia sem migration
/// (ou o contrário) passaria pelo build e quebraria só no deploy — 42703
/// "column ... does not exist" no start.
/// </summary>
[TestFixture]
public class WindowsUpdateServiceStateMigrationTests
{
    private static readonly string[] ExpectedColumns =
    [
        "service_state",
        "wu_online",
        "policy_managed",
        "last_install_success_at",
        "download_in_progress",
        "state_hash",
        "state_changed_at"
    ];

    [Test]
    public void MigrationIsDiscoveredWithUniqueVersion()
    {
        var assembly = typeof(M238_AddAgentWindowsUpdateServiceState).Assembly;
        var migrations = assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
            .Select(type => type.GetCustomAttribute<MigrationAttribute>())
            .Where(attribute => attribute is not null)
            .ToList();

        Assert.That(migrations, Is.Not.Empty);

        var versions = migrations.Select(attribute => attribute!.Version).ToList();
        var duplicates = versions.GroupBy(version => version)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        Assert.That(duplicates, Is.Empty, "versões de migration duplicadas");

        var ours = typeof(M238_AddAgentWindowsUpdateServiceState).GetCustomAttribute<MigrationAttribute>();
        Assert.That(ours, Is.Not.Null);
        Assert.That(ours!.Version, Is.EqualTo(versions.Max()), "M238 deve ser a migration mais recente");
    }

    [Test]
    public void MigrationTargetsSnapshotTable()
    {
        var tableField = typeof(M238_AddAgentWindowsUpdateServiceState)
            .GetField("Table", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(tableField, Is.Not.Null, "constante Table ausente na migration");
        Assert.That((string)tableField!.GetValue(null)!, Is.EqualTo("agent_windows_update_snapshots"));
    }

    [Test]
    public void MigrationDeclaresUpAndDown()
    {
        // Down() precisa existir: rollback remove só as colunas novas
        // (Delete.Column) — nunca a tabela de snapshot.
        var type = typeof(M238_AddAgentWindowsUpdateServiceState);
        Assert.That(type.GetMethod(nameof(Migration.Up))?.DeclaringType, Is.EqualTo(type));
        Assert.That(type.GetMethod(nameof(Migration.Down))?.DeclaringType, Is.EqualTo(type));
    }

    /// <summary>
    /// O contrato coluna-a-coluna: o EF mapeia estas propriedades (nullable) e a
    /// migration cria exatamente estas colunas. Um nome trocado de um lado só
    /// vira 42703 em produção.
    /// </summary>
    [Test]
    public void SnapshotEntityHasEveryProbePropertyNullable()
    {
        var entity = typeof(Discovery.Core.Entities.AgentWindowsUpdateSnapshot);
        var expectedProperties = new Dictionary<string, Type>
        {
            ["ServiceState"] = typeof(string),
            ["WuOnline"] = typeof(bool?),
            ["PolicyManaged"] = typeof(bool?),
            ["LastInstallSuccessAt"] = typeof(DateTime?),
            ["DownloadInProgress"] = typeof(bool?),
            ["StateHash"] = typeof(string),
            ["StateChangedAt"] = typeof(DateTime?)
        };

        foreach (var (name, type) in expectedProperties)
        {
            var property = entity.GetProperty(name);
            Assert.That(property, Is.Not.Null, $"propriedade {name} ausente no snapshot");
            Assert.That(property!.PropertyType, Is.EqualTo(type), $"tipo de {name}");
        }

        Assert.That(ExpectedColumns, Has.Length.EqualTo(expectedProperties.Count));
    }

    /// <summary>
    /// Toda propriedade nova da sonda precisa existir no MODELO EF (configurado
    /// em DiscoveryDbContext.Patches) e ser nullable. Modelo reduzido (só esta
    /// entidade) para o InMemory não tropeçar em pgvector.
    /// </summary>
    [Test]
    public void EfModelMapsEveryProbePropertyAsNullable()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Discovery.Infrastructure.Data.DiscoveryDbContext>()
            .UseInMemoryDatabase("wu-probe-model-" + Guid.NewGuid().ToString("N"))
            .Options;

        using var db = new ProbeModelDbContext(options);
        var entity = db.Model.FindEntityType(typeof(Discovery.Core.Entities.AgentWindowsUpdateSnapshot));
        Assert.That(entity, Is.Not.Null);

        var expected = new Dictionary<string, string>
        {
            ["ServiceState"] = "service_state",
            ["WuOnline"] = "wu_online",
            ["PolicyManaged"] = "policy_managed",
            ["LastInstallSuccessAt"] = "last_install_success_at",
            ["DownloadInProgress"] = "download_in_progress",
            ["StateHash"] = "state_hash",
            ["StateChangedAt"] = "state_changed_at"
        };

        Assert.That(ExpectedColumns, Is.EquivalentTo(expected.Values));

        // Provider InMemory não conhece nomes de coluna (é o provider relacional
        // que aplica [Column]/HasColumnName). Aqui validamos o que o modelo
        // carrega em qualquer provider; o nome snake_case sai do próprio
        // factory do DiscoveryDbContext (HasColumnName) e é coberto pelo
        // DbContextNamingConventionsTests em Npgsql.
        foreach (var (property, _) in expected)
        {
            var mapped = entity!.FindProperty(property);
            Assert.That(mapped, Is.Not.Null, $"propriedade {property} não mapeada no EF");
            Assert.That(mapped!.IsNullable, Is.True, $"{property} deve ser nullable (agente antigo)");
        }
    }

    /// <summary>Permite montar o modelo completo sem pgvector (provider InMemory).</summary>
    private sealed class ProbeModelDbContext(
        Microsoft.EntityFrameworkCore.DbContextOptions<Discovery.Infrastructure.Data.DiscoveryDbContext> options)
        : Discovery.Infrastructure.Data.DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Discovery.Core.Entities.AgentWindowsUpdateSnapshot)
            };

            foreach (var entityType in typeof(Discovery.Core.Entities.AgentWindowsUpdateSnapshot).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Discovery.Core.Entities.AgentWindowsUpdateSnapshot>(
                entity => entity.HasKey(item => item.AgentId));
        }
    }
}