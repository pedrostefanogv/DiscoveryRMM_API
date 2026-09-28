using System.Reflection;
using Discovery.Migrations.Migrations;
using FluentMigrator;

namespace Discovery.Tests;

/// <summary>
/// Guarda de integridade das migrations FluentMigrator.
///
/// O schema é gerido por FluentMigrator (MigrateUp no startup da API), não por
/// migrations do EF Core. Uma versão de migration duplicada quebra o MigrateUp
/// em produção — risco real porque o repositório tem muitos arquivos e os
/// prefixos numéricos se repetem (ex.: M060/M120/M169 com versões distintas).
/// </summary>
public class MigrationIntegrityTests
{
    private static Assembly MigrationsAssembly => typeof(M001_CreateClients).Assembly;

    [Test]
    public void Migration_versions_are_unique()
    {
        var migrations = MigrationsAssembly.GetTypes()
            .Select(type => new { Type = type, Attribute = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attribute is not null)
            .Select(item => new { item.Type, Version = item.Attribute!.Version })
            .ToList();

        Assert.That(migrations, Is.Not.Empty, "nenhuma migration encontrada no assembly");

        var duplicates = migrations
            .GroupBy(migration => migration.Version)
            .Where(group => group.Count() > 1)
            .Select(group => $"version={group.Key}: {string.Join(", ", group.Select(m => m.Type.Name))}")
            .ToList();

        Assert.That(duplicates, Is.Empty, "versões de migration duplicadas quebram o MigrateUp");
    }

    [Test]
    public void All_migrations_derive_from_Migration()
    {
        var invalid = MigrationsAssembly.GetTypes()
            .Where(type => type.GetCustomAttribute<MigrationAttribute>() is not null)
            .Where(type => !typeof(Migration).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToList();

        Assert.That(invalid, Is.Empty);
    }
}
