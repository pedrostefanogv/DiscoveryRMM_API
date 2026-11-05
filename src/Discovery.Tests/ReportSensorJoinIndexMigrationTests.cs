using System.Reflection;
using Discovery.Migrations.Migrations;
using FluentMigrator;

namespace Discovery.Tests;

/// <summary>
/// Guarda da migration M270 (indices de join de sensores nos relatorios).
///
/// O schema e gerido por FluentMigrator (MigrateUp no startup da API), nao pelo
/// EF Core. Aqui garantimos que:
///  - a migration existe e e descoberta pelo runner;
///  - a versao e MAIOR que a anterior (uma versao menor nunca seria aplicada);
///  - ela NAO recria os indices que ja existem desde M260;
///  - o Up e o Down sao idempotentes (guardados por Schema...Exists()).
/// </summary>
[TestFixture]
public class ReportSensorJoinIndexMigrationTests
{
    private static string MigrationSource()
    {
        // Sobe do diretorio de saida do teste ate a raiz do repo, como fazem os
        // testes de migration existentes.
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Discovery.Migrations")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "raiz do repositorio nao encontrada");

        var path = Path.Combine(
            directory!.FullName, "src", "Discovery.Migrations", "Migrations",
            "M270_AddReportSensorJoinIndexes.cs");

        Assert.That(File.Exists(path), Is.True, $"migration nao encontrada em {path}");
        return File.ReadAllText(path);
    }

    [Test]
    public void M270_ShouldBeDiscoverableByTheMigrationRunner()
    {
        var type = typeof(M270_AddReportSensorJoinIndexes);
        var attribute = type.GetCustomAttribute<MigrationAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(attribute, Is.Not.Null, "Sem [Migration] o runner ignora o arquivo.");
            Assert.That(typeof(Migration).IsAssignableFrom(type), Is.True);
        });
    }

    [Test]
    public void M270_ShouldHaveAVersionGreaterThanM269()
    {
        var migrations = typeof(M001_CreateClients).Assembly.GetTypes()
            .Select(type => new { Type = type, Attribute = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attribute is not null)
            .Select(item => new { item.Type, Version = item.Attribute!.Version })
            .ToList();

        var m269 = migrations.Single(m => m.Type == typeof(M269_CreateAutoTicketSuggestions));
        var m270 = migrations.Single(m => m.Type == typeof(M270_AddReportSensorJoinIndexes));

        Assert.That(m270.Version, Is.GreaterThan(m269.Version),
            "Uma migration com versao menor que a anterior NUNCA e aplicada pelo FluentMigrator.");
    }

    [Test]
    public void M270_ShouldNotRecreateIndexesThatAlreadyExist()
    {
        var source = MigrationSource();

        // Ja existem desde M260 / DiscoveryDbContext.Sensors.cs: recriar
        // quebraria o MigrateUp em bases que ja os tem. A guarda olha as CHAMADAS
        // de Create.Index, nao o texto: o cabecalho cita os nomes justamente
        // para documentar que eles NAO sao recriados aqui.
        var createdIndexes = System.Text.RegularExpressions.Regex
            // '"' dentro de string verbatim nao e escape: usa classe de caracteres.
            .Matches(source, @"Create\.Index\([""]([^""]+)[""]\)")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.That(createdIndexes, Does.Not.Contain("ux_agent_sensor_states_agent_sensor_target"),
            "a M270 nao deve recriar o indice unico ja criado na M260");
        Assert.That(createdIndexes, Does.Not.Contain("ix_agent_sensor_states_agent_status"),
            "a M270 nao deve recriar o indice ja criado na M260");
    }

    [Test]
    public void M270_UpAndDownShouldBeIdempotent()
    {
        var source = MigrationSource();

        // Todo Create/Delete de indice precisa estar sob uma guarda Exists().
        var creates = System.Text.RegularExpressions.Regex.Matches(source, @"Create\.Index\(").Count;
        var deletes = System.Text.RegularExpressions.Regex.Matches(source, @"Delete\.Index\(").Count;
        var guards = System.Text.RegularExpressions.Regex.Matches(source, @"\.Index\([A-Za-z0-9_]+\)\.Exists\(\)").Count;

        Assert.That(creates, Is.GreaterThan(0), "a M270 precisa criar ao menos um indice");
        Assert.That(guards, Is.GreaterThanOrEqualTo(creates + deletes),
            "cada Create/Delete de indice precisa estar protegido por .Exists()");
    }

    [Test]
    public void M270_ShouldTargetTheSensorJoinTables()
    {
        var source = MigrationSource();

        Assert.That(source, Does.Contain("agent_sensor_states"));
        Assert.That(source, Does.Contain("sensor_definitions"));
    }
}
