using System.Reflection;
using System.Text.Json;
using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Discovery.Migrations.Migrations;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Valida os templates embutidos de sensores (M271).
///
/// O ponto central NAO e "o JSON e parseavel": e que o layoutJson de cada
/// template passe no <see cref="ReportLayoutFieldResolver"/> de verdade,
/// contra o catalogo real. O backend rejeita com 400 (campo layoutJson)
/// layouts com campo inexistente, join sem sourceKey, joinToAlias que nao
/// seja fonte anterior ou chave ausente em um dos lados — um seed que viole
/// isso nasce quebrado para o usuario.
/// </summary>
[TestFixture]
public class SeedSensorReportTemplatesTests
{
    private const string SensorFailuresId = "a0000004-0000-0000-0000-000000000001";
    private const string HardwareSensorsId = "a0000004-0000-0000-0000-000000000002";

    private static readonly IReadOnlyList<ReportDatasetCatalogItemDto> Catalog =
        new ReportDatasetCatalogProvider().GetAll();

    /// <summary>
    /// Le a MESMA string de layout que o seed insere. As propriedades sao
    /// publicas na migration e usadas no proprio Up(), entao nao ha copia
    /// divergente entre o que e gravado e o que e validado aqui.
    /// </summary>
    private static string ReadLayoutJson(int seedNumber) => seedNumber switch
    {
        1 => M271_SeedSensorReportTemplates.SensorFailuresLayoutJson,
        2 => M271_SeedSensorReportTemplates.HardwareSensorsLayoutJson,
        _ => throw new ArgumentOutOfRangeException(nameof(seedNumber))
    };

    private static ReportLayoutDefinition ParseLayout(string json)
        => JsonSerializer.Deserialize<ReportLayoutDefinition>(json, Options)!;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── Contrato com o resolver real ────────────────────────

    [TestCase(1, TestName = "SensorFailures_Layout_IsAcceptedByResolver")]
    [TestCase(2, TestName = "HardwareSensors_Layout_IsAcceptedByResolver")]
    public void SeedLayout_IsAcceptedByFieldResolver(int seedNumber)
    {
        var layout = ParseLayout(ReadLayoutJson(seedNumber));
        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        Assert.That(errors, Is.Empty,
            $"layout do seed {seedNumber} seria rejeitado pelo backend: {string.Join(" | ", errors)}");
    }

    [Test]
    public void HardwareSensors_IsMultiSourceWithLeftJoinOnAgentId()
    {
        var layout = ParseLayout(ReadLayoutJson(2));

        Assert.That(layout.DataSources, Is.Not.Null);
        Assert.That(layout.DataSources!.Count, Is.EqualTo(2), "precisa das duas fontes");

        var baseSource = layout.DataSources[0];
        var joined = layout.DataSources[1];

        Assert.That(baseSource.DatasetType, Is.EqualTo(ReportDatasetType.AgentHardware));
        Assert.That(baseSource.Alias, Is.EqualTo("hw"));
        Assert.That(baseSource.Join, Is.Null, "a primeira fonte e a base: sem join");

        Assert.That(joined.DatasetType, Is.EqualTo(ReportDatasetType.AgentSensors));
        Assert.That(joined.Alias, Is.EqualTo("sns"));
        Assert.That(joined.Join, Is.Not.Null);
        Assert.That(joined.Join!.JoinToAlias, Is.EqualTo("hw"), "joinToAlias precisa ser a fonte anterior");
        Assert.That(joined.Join.SourceKey, Is.EqualTo("agentId"));
        Assert.That(joined.Join.TargetKey, Is.EqualTo("agentId"));
        Assert.That(joined.Join.JoinType, Is.EqualTo("left"));
    }

    [Test]
    public void HardwareSensors_UsesOnlyFieldsThatExistInEachDataset()
    {
        var layout = ParseLayout(ReadLayoutJson(2));
        var hardware = FindByType(4);
        var sensors = FindByType(29);

        foreach (var column in layout.Columns ?? [])
        {
            Assert.That(column.Field, Does.StartWith("hw."), $"coluna principal fora da base: {column.Field}");
            var field = column.Field!["hw.".Length..];
            Assert.That(hardware.Fields, Does.Contain(field), $"'{field}' nao existe em agentHardware");
        }

        Assert.That(layout.Sections, Is.Not.Null.And.Not.Empty);
        foreach (var section in layout.Sections!)
        {
            foreach (var column in section.Columns ?? [])
            {
                // O modelo do backend (ReportLayoutSectionDefinition) NAO tem
                // "source": o vinculo com a fonte e o prefixo do proprio campo.
                Assert.That(column.Field, Does.StartWith("sns."));
                var field = column.Field!["sns.".Length..];
                Assert.That(sensors.Fields, Does.Contain(field), $"'{field}' nao existe em agentSensors");
            }
        }
    }

    [Test]
    public void HardwareSensors_DoesNotUseClientIdOrSiteId()
    {
        // agentHardware expoe clientName/siteName, NAO clientId/siteId. Usar
        // clientId no join seria rejeitado pelo resolver.
        var layout = ParseLayout(ReadLayoutJson(2));
        var hardware = FindByType(4);

        Assert.That(hardware.Fields, Does.Not.Contain("clientId"));
        Assert.That(hardware.Fields, Does.Not.Contain("siteId"));
        Assert.That(layout.DataSources![1].Join!.SourceKey, Is.EqualTo("agentId"));
    }

    [Test]
    public void SensorFailures_IsSingleSourceGroupedByHostname()
    {
        var layout = ParseLayout(ReadLayoutJson(1));

        // Fonte unica: o dataset vem do proprio template (dataset_type = 29),
        // NAO de dataSources. E o mesmo formato dos templates de fonte unica do
        // M232 — declarar dataSources aqui seria redundante e divergente.
        Assert.That(layout.DataSources, Is.Null,
            "template de fonte unica nao declara dataSources");
        Assert.That(layout.GroupBy, Is.EqualTo("agentHostname"));
        Assert.That(layout.GroupTitleTemplate, Is.Not.Null.And.Not.Empty);
        Assert.That(layout.Columns, Is.Not.Null.And.Not.Empty);
        Assert.That(layout.HideGroupColumn, Is.True);

        // Nomes puros (sem alias) sao o formato correto em fonte unica, e o
        // resolver os aceita nesse caso.
        Assert.That(layout.Columns!.Select(c => c.Field), Does.Contain("sensorName"));
        Assert.That(layout.Columns!.Select(c => c.Field), Does.Not.Contain("sns.sensorName"));
    }

    [Test]
    public void SensorFailures_UsesOnlyExistingSensorFields()
    {
        var layout = ParseLayout(ReadLayoutJson(1));
        var sensors = FindByType(29);

        foreach (var column in layout.Columns ?? [])
        {
            Assert.That(sensors.Fields, Does.Contain(column.Field!),
                $"'{column.Field}' nao existe em agentSensors");
        }
    }

    [Test]
    public void HardwareSensors_SectionsKeepUiSourceHint()
    {
        // "source" NAO existe em ReportLayoutSectionDefinition (o backend o
        // ignora, e o vinculo real e o prefixo "sns." do campo). Mesmo assim o
        // console o usa como estado do seletor de fonte da secao
        // (report-template-form/sections.tsx) e o reescreve ao salvar, entao o
        // seed precisa mante-lo para o round-trip no editor.
        using var document = JsonDocument.Parse(ReadLayoutJson(2));
        var sections = document.RootElement.GetProperty("sections");
        Assert.That(sections.GetArrayLength(), Is.EqualTo(1));

        var section = sections[0];
        Assert.That(section.TryGetProperty("source", out var source), Is.True,
            "o editor do console depende de section.source");
        Assert.That(source.GetString(), Is.EqualTo("sns"));
    }

    // ── IDs / determinismo ──────────────────────────────────

    [Test]
    public void SeedIds_AreDeterministicAndDistinct()
    {
        Assert.That(Guid.Parse(SensorFailuresId), Is.Not.EqualTo(Guid.Parse(HardwareSensorsId)));
        Assert.That(SensorFailuresId, Does.StartWith("a0000004-"));
        Assert.That(HardwareSensorsId, Does.StartWith("a0000004-"));
    }

    [Test]
    public void MigrationId_FollowsSequenceAfter270()
    {
        var attribute = typeof(M271_SeedSensorReportTemplates)
            .GetCustomAttribute<FluentMigrator.MigrationAttribute>();

        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute!.Version, Is.EqualTo(20270106_271));
    }

    private static ReportDatasetCatalogItemDto FindByType(int datasetType)
        => Catalog.First(item => item.DatasetType == datasetType);
}
