using System.Reflection;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Migrations.Migrations;
using FluentMigrator;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Guarda de schema do DIAGNÓSTICO do scan e da sonda estendida (M239).
///
/// Mesmo motivo da guarda da M238: o FluentMigrator só roda em banco real, então
/// um [Migration] com versão repetida ou uma propriedade mapeada no EF sem
/// coluna correspondente passaria pelo build e quebraria no deploy com 42703.
/// </summary>
[TestFixture]
public class WindowsUpdateScanDiagnosticsMigrationTests
{
    /// <summary>Colunas do bloco "scan" — nullable, exceto o contador.</summary>
    private static readonly Dictionary<string, Type> ScanProperties = new()
    {
        ["ScanErrorCode"] = typeof(string),
        ["ScanTrigger"] = typeof(string),
        ["ScanSource"] = typeof(string),
        ["SkippedCount"] = typeof(int),
        ["SkippedReasons"] = typeof(string)
    };

    /// <summary>Colunas da sonda estendida (state): todas nullable.</summary>
    private static readonly Dictionary<string, Type> StateProperties = new()
    {
        ["WsusServer"] = typeof(string),
        ["AuOptions"] = typeof(int?),
        ["NoAutoUpdate"] = typeof(bool?),
        ["DeferralDays"] = typeof(int?),
        ["ActiveHoursStart"] = typeof(int?),
        ["ActiveHoursEnd"] = typeof(int?),
        ["QfeInstalledCount"] = typeof(int?),
        ["DownloadPercent"] = typeof(int?)
    };

    [Test]
    public void MigrationIsDiscoveredWithUniqueVersion()
    {
        var assembly = typeof(M239_AddWindowsUpdateScanDiagnostics).Assembly;
        var migrations = assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
            .Select(type => type.GetCustomAttribute<MigrationAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Version)
            .ToList();

        Assert.That(migrations, Is.Not.Empty);
        Assert.That(migrations.Distinct().Count(), Is.EqualTo(migrations.Count), "versões de migration duplicadas");

        var ours = typeof(M239_AddWindowsUpdateScanDiagnostics).GetCustomAttribute<MigrationAttribute>();
        Assert.That(ours, Is.Not.Null);
        Assert.That(migrations, Does.Contain(ours!.Version));
        Assert.That(ours.Version, Is.GreaterThan(
            typeof(M238_AddAgentWindowsUpdateServiceState).GetCustomAttribute<MigrationAttribute>()!.Version),
            "M239 precisa rodar depois da M238");
    }

    [Test]
    public void MigrationTargetsSnapshotTableAndDeclaresUpDown()
    {
        var type = typeof(M239_AddWindowsUpdateScanDiagnostics);
        var tableField = type.GetField("Table", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(tableField, Is.Not.Null);
        Assert.That((string)tableField!.GetValue(null)!, Is.EqualTo("agent_windows_update_snapshots"));
        Assert.That(type.GetMethod(nameof(Migration.Up))?.DeclaringType, Is.EqualTo(type));
        Assert.That(type.GetMethod(nameof(Migration.Down))?.DeclaringType, Is.EqualTo(type));
    }

    [Test]
    public void EntityHasEveryDiagnosticProperty()
    {
        var entity = typeof(AgentWindowsUpdateSnapshot);
        foreach (var (name, expectedType) in ScanProperties.Concat(StateProperties))
        {
            var property = entity.GetProperty(name);
            Assert.That(property, Is.Not.Null, $"propriedade {name} ausente no snapshot");
            Assert.That(property!.PropertyType, Is.EqualTo(expectedType), $"tipo de {name}");
        }
    }

    [Test]
    public void EfModelMapsEveryDiagnosticProperty()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("wu-diagnostics-model-" + Guid.NewGuid().ToString("N"))
            .Options;

        using var db = new ProbeModelDbContext(options);
        var entity = db.Model.FindEntityType(typeof(AgentWindowsUpdateSnapshot));
        Assert.That(entity, Is.Not.Null);

        // Diagnóstico do scan: só o contador é NOT NULL (0 é valor válido).
        Assert.That(entity!.FindProperty("SkippedCount"), Is.Not.Null);
        Assert.That(entity.FindProperty("SkippedCount")!.IsNullable, Is.False, "skipped_count é NOT NULL DEFAULT 0");
        foreach (var name in new[] { "ScanErrorCode", "ScanTrigger", "ScanSource", "SkippedReasons" })
        {
            var mapped = entity.FindProperty(name);
            Assert.That(mapped, Is.Not.Null, $"propriedade {name} não mapeada no EF");
            Assert.That(mapped!.IsNullable, Is.True, $"{name} deve ser nullable (agente antigo)");
        }

        // Sonda estendida: tudo nullable — campo ausente nunca zera o conhecido.
        foreach (var name in StateProperties.Keys)
        {
            var mapped = entity.FindProperty(name);
            Assert.That(mapped, Is.Not.Null, $"propriedade {name} não mapeada no EF");
            Assert.That(mapped!.IsNullable, Is.True, $"{name} deve ser nullable");
        }
    }

    /// <summary>
    /// Nome e tipo de cada coluna no modelo RELACIONAL: um HasColumnName errado
    /// (ex.: "skipped_counts") ou um tamanho menor que a migration passariam nos
    /// testes de nullability e quebrariam no deploy com 42703/truncamento.
    /// </summary>
    [Test]
    public void RelationalModelUsesMigrationColumnNamesAndTypes()
    {
        // Modelo COMPLETO com provider Npgsql (sem abrir conexão): é o único
        // jeito de ler os HasColumnName/HasColumnType definidos em
        // DiscoveryDbContext.Patches — o modelo reduzido dos outros testes não
        // passa por essa configuração.
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=model_only;Username=x;Password=x",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        using var db = new FullModelDbContext(options);
        var entity = db.Model.FindEntityType(typeof(AgentWindowsUpdateSnapshot));
        Assert.That(entity, Is.Not.Null);

        var expected = new Dictionary<string, string>
        {
            ["ScanErrorCode"] = "scan_error_code",
            ["ScanTrigger"] = "scan_trigger",
            ["ScanSource"] = "scan_source",
            ["SkippedCount"] = "skipped_count",
            ["SkippedReasons"] = "skipped_reasons",
            ["WsusServer"] = "wsus_server",
            ["AuOptions"] = "au_options",
            ["NoAutoUpdate"] = "no_auto_update",
            ["DeferralDays"] = "deferral_days",
            ["ActiveHoursStart"] = "active_hours_start",
            ["ActiveHoursEnd"] = "active_hours_end",
            ["QfeInstalledCount"] = "qfe_installed_count",
            ["DownloadPercent"] = "download_percent"
        };

        foreach (var (property, column) in expected)
        {
            var mapped = entity!.FindProperty(property);
            Assert.That(mapped, Is.Not.Null, $"propriedade {property} não mapeada");
            Assert.That(mapped!.GetColumnName(), Is.EqualTo(column), $"coluna de {property}");
        }

        // Tamanhos e tipos declarados (migration usa varchar(20)/varchar(500)/jsonb).
        Assert.That(entity!.FindProperty("ScanTrigger")!.GetMaxLength(), Is.EqualTo(20));
        Assert.That(entity.FindProperty("ScanSource")!.GetMaxLength(), Is.EqualTo(20));
        Assert.That(entity.FindProperty("WsusServer")!.GetMaxLength(), Is.EqualTo(500));
        Assert.That(entity.FindProperty("SkippedReasons")!.GetColumnType(), Is.EqualTo("jsonb"));
        Assert.That(entity.FindProperty("SkippedCount")!.GetColumnType(), Is.EqualTo("integer"));
    }

    /// <summary>Modelo completo (todos os Configure*) com provider Npgsql, sem conexão.</summary>
    private sealed class FullModelDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options);

    /// <summary>Modelo reduzido (sem pgvector) para o provider InMemory.</summary>
    private sealed class ProbeModelDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(AgentWindowsUpdateSnapshot) };

            foreach (var entityType in typeof(AgentWindowsUpdateSnapshot).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AgentWindowsUpdateSnapshot>(entity => entity.HasKey(item => item.AgentId));
        }
    }
}
