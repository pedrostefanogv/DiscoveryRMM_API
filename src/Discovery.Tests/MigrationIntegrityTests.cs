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

    /// <summary>
    /// Vazios históricos conhecidos na sequência de versões. Cada entrada precisa
    /// estar documentada aqui com o motivo — nova lacuna na numeração indica
    /// migration renomeada/apagada sem revisão e quebra o rastreio do schema.
    /// (Auditoria 2026-10-21: 25-29, 147-151 e 189-190 são renumerações antigas;
    /// M237 nunca existiu nos fontes nem na documentação arquivada.)
    /// </summary>
    // 239 e 240: reservadas para outro agente (não criadas neste checkout ainda);
    // M241/M242 (audit chain e runbook) preenchem 241-242.
    private static readonly int[] KnownVersionGaps = { 25, 26, 27, 28, 29, 147, 148, 149, 150, 151, 189, 190, 237, 239, 240 };

    [Test]
    public void Migration_versions_have_no_undocumented_gaps()
    {
        // Esquema real do projeto (decifrado na auditoria 2026-10-21): Version é
        // YYYYMMDD_SSS — ex. [Migration(20260301_001)] — e NÃO o número do nome.
        // Portanto a guarda é por NOME (M### contíguo, lacunas documentadas) e por
        // sanidade do formato da Version (12 dígitos, data plausível).
        var named = MigrationsAssembly.GetTypes()
            .Select(type => new { Name = type.Name, Attr = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attr is not null && item.Name.Length >= 4 && item.Name[0] == 'M' && char.IsDigit(item.Name[1]))
            .ToList();

        Assert.That(named, Is.Not.Empty, "nenhuma migration M### encontrada");

        // 1) Version dentro do formato YYYYMMDD_SSS (evita truncamento/engano).
        var badFormat = named
            .Where(item => item.Attr!.Version < 20200101_001L || item.Attr!.Version > 20991231_999L)
            .Select(item => item.Name + ": Version=" + item.Attr!.Version)
            .ToList();
        Assert.That(badFormat, Is.Empty,
            "Version fora do formato YYYYMMDD_SSS: " + string.Join(", ", badFormat));

        // 2) Contiguidade documentada da numeração do NOME.
        var numbers = named
            .Select(item => int.Parse(System.Text.RegularExpressions.Regex.Match(item.Name, "^M(\\d{3})_").Groups[1].Value))
            .ToHashSet();
        var lowest = numbers.Min();
        var highest = numbers.Max();

        var unexpected = Enumerable.Range(lowest, highest - lowest + 1)
            .Where(v => !numbers.Contains(v) && !KnownVersionGaps.Contains(v))
            .ToList();

        Assert.That(unexpected, Is.Empty,
            "lacunas não documentadas em KnownVersionGaps: " + string.Join(", ", unexpected));
    }

    /// <summary>
    /// A Version (YYYYMMDD_SSS) precisa CRESCER junto com o número do nome (M###).
    /// Caso real (2026-10-31): M252 recebeu 20261029_252 e M253 recebeu
    /// 20261030_253 — versões MENORES que M241..M251 (20261222..20261230).
    /// Dependendo do ambiente isso aplica fora de ordem ou não aplica a migração,
    /// deixando o EF mapeando colunas inexistentes (42703 → HTTP 500).
    /// </summary>
    [Test]
    public void Migration_versions_must_grow_with_the_migration_number()
    {
        // A numeração anterior à M238 tem violações históricas conhecidas (ex.:
        // M060..M062, M090..M135, M147/M152, M209/M210) — legado aceito, não é o
        // alvo desta guarda. A partir da M238 a Version precisa ser monotônica,
        // que é exatamente a faixa onde o erro de 2026-10-31 aconteceu.
        const int FirstEnforcedNumber = 238;

        var named = MigrationsAssembly.GetTypes()
            .Select(type => new { Name = type.Name, Attr = type.GetCustomAttribute<MigrationAttribute>() })
            .Where(item => item.Attr is not null && item.Name.Length >= 4 && item.Name[0] == 'M' && char.IsDigit(item.Name[1]))
            .Select(item => new
            {
                item.Name,
                Number = int.Parse(System.Text.RegularExpressions.Regex.Match(item.Name, "^M(\\d{3})_").Groups[1].Value),
                Version = item.Attr!.Version
            })
            .Where(item => item.Number >= FirstEnforcedNumber)
            .OrderBy(item => item.Number)
            .ToList();

        var regressions = named
            .Zip(named.Skip(1))
            .Where(pair => pair.Second.Version <= pair.First.Version)
            .Select(pair => $"{pair.First.Name}({pair.First.Version}) >= {pair.Second.Name}({pair.Second.Version})")
            .ToList();

        Assert.That(regressions, Is.Empty,
            "Version fora de ordem: uma migração nova precisa ter Version maior que a anterior. Ajuste: " +
            string.Join(", ", regressions));
    }
}
