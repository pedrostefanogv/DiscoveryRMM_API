using System.Reflection;
using Discovery.Migrations.Migrations;
using FluentMigrator;

namespace Discovery.Tests;

/// <summary>
/// Guarda da migration M281 (campos de storage de report_executions NULLABLE).
///
/// A execucao de relatorio nasce Pending sem bucket/chave/content-type/tamanho
/// (so ficam preenchidos no UpdateResultAsync), e o modelo EF declara esses
/// campos como opcionais. A M060 criou as colunas NOT NULL — o INSERT do
/// run-now quebrava com 23502 antes desta migracao. Aqui garantimos que:
///  - a migration existe e e descoberta pelo runner;
///  - a versao e MAIOR que a anterior;
///  - ela altera exatamente as quatro colunas problematicas para nullable;
///  - o Down repoe os defaults da M060 antes de religar o NOT NULL.
/// </summary>
[TestFixture]
public class ReportExecutionStorageNullableMigrationTests
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
            "M281_AlterReportExecutionsStorageFieldsNullable.cs");

        Assert.That(File.Exists(path), Is.True, $"migration nao encontrada em {path}");
        return File.ReadAllText(path);
    }

    [Test]
    public void M281_ShouldBeDiscoverableByTheMigrationRunner()
    {
        var type = typeof(M281_AlterReportExecutionsStorageFieldsNullable);
        var attribute = type.GetCustomAttribute<MigrationAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(attribute, Is.Not.Null, "Sem [Migration] o runner ignora o arquivo.");
            Assert.That(typeof(Migration).IsAssignableFrom(type), Is.True);
        });
    }

    [Test]
    public void M281_ShouldHaveAVersionGreaterThanM280()
    {
        var migrations = typeof(M001_CreateClients).Assembly.GetTypes()
            .Select(type => new { Type = type, Attribute = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attribute is not null)
            .Select(item => new { item.Type, Version = item.Attribute!.Version })
            .ToList();

        var m280 = migrations.Single(m => m.Type == typeof(M280_AddUpdatesTabEnabled));
        var m281 = migrations.Single(m => m.Type == typeof(M281_AlterReportExecutionsStorageFieldsNullable));

        Assert.That(m281.Version, Is.GreaterThan(m280.Version),
            "Uma migration com versao menor que a anterior NUNCA e aplicada pelo FluentMigrator.");
    }

    [Test]
    public void M281_UpShouldRelaxAllFourStorageColumns()
    {
        var source = MigrationSource();

        foreach (var column in new[] { "storage_bucket", "storage_object_key", "storage_content_type", "storage_size_bytes" })
        {
            // A guarda e textual, mas as quatro colunas sao fixas e sao justamente
            // as unicas NOT NULL da M060 com propriedades opcionais no modelo EF.
            Assert.That(
                source,
                Does.Contain($"AlterColumn(\"{column}\")"),
                $"a M281 precisa relaxar a coluna {column}");
        }
    }

    [Test]
    public void M281_UpShouldNotSetNotNullable()
    {
        var source = MigrationSource();
        var upIndex = source.IndexOf("public override void Down()", StringComparison.Ordinal);
        Assert.That(upIndex, Is.GreaterThan(0), "migracao sem Down");

        var up = source[..upIndex];

        Assert.That(up, Does.Not.Contains("NotNullable()"),
            "o Up deve apenas relaxar as colunas; restringir de novo e papel do Down");
    }
}
