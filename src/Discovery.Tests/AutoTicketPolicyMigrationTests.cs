using System.Reflection;
using Discovery.Migrations.Migrations;
using FluentMigrator;

namespace Discovery.Tests;

/// <summary>
/// Guarda específica da migration M267 (política de auto-ticket).
///
/// O schema é gerido por FluentMigrator (MigrateUp no startup da API), não pelo
/// EF Core. Aqui garantimos que:
///  - a migration existe e é descoberta pelo runner;
///  - ela é a MAIOR versão da sequência (uma versão menor nunca seria aplicada);
///  - as três colunas de política estão declaradas no código da migration.
/// </summary>
[TestFixture]
public class AutoTicketPolicyMigrationTests
{
    [Test]
    public void M267_ShouldBeDiscoverableByTheMigrationRunner()
    {
        var type = typeof(M267_AddAutoTicketPolicy);
        var attribute = type.GetCustomAttribute<MigrationAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(attribute, Is.Not.Null, "Sem [Migration] o runner ignora o arquivo.");
            Assert.That(typeof(Migration).IsAssignableFrom(type), Is.True);
        });
    }

    [Test]
    public void M267_ShouldHaveAVersionGreaterThanThePreviousMigration()
    {
        var migrations = typeof(M001_CreateClients).Assembly.GetTypes()
            .Select(type => new { Type = type, Attribute = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attribute is not null)
            .Select(item => new { item.Type, Version = item.Attribute!.Version })
            .ToList();

        var m266 = migrations.Single(m => m.Type == typeof(M266_AddSustainSamplesToSensorDefinitions));
        var m267 = migrations.Single(m => m.Type == typeof(M267_AddAutoTicketPolicy));

        Assert.That(m267.Version, Is.GreaterThan(m266.Version),
            "Uma migration com versão menor que a anterior NUNCA é aplicada pelo FluentMigrator.");
    }

    [Test]
    public void M268_ShouldHaveAVersionGreaterThanM267()
    {
        var migrations = typeof(M001_CreateClients).Assembly.GetTypes()
            .Select(type => new { Type = type, Attribute = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attribute is not null)
            .Select(item => new { item.Type, Version = item.Attribute!.Version })
            .ToList();

        var m267 = migrations.Single(m => m.Type == typeof(M267_AddAutoTicketPolicy));
        var m268 = migrations.Single(m => m.Type == typeof(M268_AddSensorAutoTicketColumns));

        Assert.That(m268.Version, Is.GreaterThan(m267.Version));
    }

    [Test]
    public void M269_ShouldHaveAVersionGreaterThanM268()
    {
        var migrations = typeof(M001_CreateClients).Assembly.GetTypes()
            .Select(type => new { Type = type, Attribute = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attribute is not null)
            .Select(item => new { item.Type, Version = item.Attribute!.Version })
            .ToList();

        var m268 = migrations.Single(m => m.Type == typeof(M268_AddSensorAutoTicketColumns));
        var m269 = migrations.Single(m => m.Type == typeof(M269_CreateAutoTicketSuggestions));

        Assert.That(m269.Version, Is.GreaterThan(m268.Version));
    }

    [Test]
    public void M269_ShouldCreateTheSuggestionAuditTable()
    {
        var path = ResolveMigrationSource("M269_CreateAutoTicketSuggestions.cs");
        Assert.That(path, Is.Not.Null);

        var source = File.ReadAllText(path!);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("auto_ticket_suggestions"));
            // Colunas que tornam a taxa de override mensurável.
            Assert.That(source, Does.Contain("confidence"));
            Assert.That(source, Does.Contain("applied"));
            Assert.That(source, Does.Contain("not_applied_reason"));
            Assert.That(source, Does.Contain("overridden_at"));
            Assert.That(source, Does.Contain("overridden_by_user_id"));
            Assert.That(source, Does.Contain("public override void Down()"));
        });
    }

    [Test]
    public void M268_ShouldTargetTheSensorDefinitionTable()
    {
        var path = ResolveMigrationSource("M268_AddSensorAutoTicketColumns.cs");
        Assert.That(path, Is.Not.Null);

        var source = File.ReadAllText(path!);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("sensor_definitions"));
            Assert.That(source, Does.Contain("auto_ticket_policy"));
            Assert.That(source, Does.Contain("auto_ticket_min_duration_seconds"));
            Assert.That(source, Does.Contain("auto_ticket_impact"));
            Assert.That(source, Does.Contain("public override void Down()"));
        });
    }

    [Test]
    public void M267_ShouldTargetTheThreeConfigurationLevels()
    {
        // Lê o fonte da migration para confirmar as três tabelas. É intencional
        // ser textual: prova que o arquivo declara exatamente os três níveis da
        // herança (Server -> Client -> Site) e a coluna esperada.
        var path = ResolveMigrationSource();
        Assert.That(path, Is.Not.Null, "Não foi possível localizar o fonte da migration.");

        var source = File.ReadAllText(path!);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("server_configurations"));
            Assert.That(source, Does.Contain("client_configurations"));
            Assert.That(source, Does.Contain("site_configurations"));
            Assert.That(source, Does.Contain("auto_ticket_policy_json"));
            Assert.That(source, Does.Contain("jsonb"));
            Assert.That(source, Does.Contain("public override void Down()"),
                "Migration precisa de Down() para reverter.");
            Assert.That(source, Does.Contain("20270102_267"),
                "A versão precisa ser maior que a migration anterior (20270101_266).");
        });
    }

    /// <summary>
    /// As migrations do FluentMigrator descrevem o schema, e o EF descreve o
    /// mapeamento. Nada garante que os dois concordem — quando divergem, o
    /// sintoma e truncamento silencioso ou coluna com tamanho errado.
    ///
    /// Este teste trava o contrato das colunas de texto da
    /// auto_ticket_suggestions (M269), que guardam valores sugeridos pela IA.
    /// </summary>
    [TestCase("suggested_value", 500)]
    [TestCase("applied_value", 500)]
    [TestCase("strategy_source", 50)]
    [TestCase("rationale", 1000)]
    [TestCase("model", 200)]
    [TestCase("not_applied_reason", 500)]
    public void M269_ColumnLengths_ShouldMatchTheEfMapping(string column, int expectedLength)
    {
        var migration = ResolveMigrationSource("M269_CreateAutoTicketSuggestions.cs");
        var mapping = ResolveSource("Discovery.Infrastructure", "Data", "DiscoveryDbContext.P2pAndAlerts.cs");
        Assert.That(migration, Is.Not.Null);
        Assert.That(mapping, Is.Not.Null);

        var migrationSource = File.ReadAllText(migration!);
        var mappingSource = File.ReadAllText(mapping!);

        // Na migration: .WithColumn("<col>").AsString(<n>)
        var migrationMatch = System.Text.RegularExpressions.Regex.Match(
            migrationSource,
            "\\\"" + column + "\\\"\\s*\\)\\.AsString\\((\\d+)\\)");

        // No EF: .HasColumnName("<col>").HasMaxLength(<n>)
        var mappingMatch = System.Text.RegularExpressions.Regex.Match(
            mappingSource,
            "HasColumnName\\(\\\"" + column + "\\\"\\)\\.HasMaxLength\\((\\d+)\\)");

        Assert.Multiple(() =>
        {
            Assert.That(migrationMatch.Success, Is.True,
                $"Coluna {column} nao encontrada na migration M269 com AsString.");
            Assert.That(mappingMatch.Success, Is.True,
                $"Coluna {column} nao encontrada no mapeamento EF com HasMaxLength.");
            Assert.That(int.Parse(migrationMatch.Groups[1].Value), Is.EqualTo(expectedLength),
                $"Migration define {column} com tamanho diferente do esperado.");
            Assert.That(int.Parse(mappingMatch.Groups[1].Value), Is.EqualTo(expectedLength),
                $"EF define {column} com tamanho diferente do esperado.");
        });
    }

    private static string? ResolveSource(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName, "src" }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? ResolveMigrationSource(string fileName = "M267_AddAutoTicketPolicy.cs")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "Discovery.Migrations", "Migrations", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}