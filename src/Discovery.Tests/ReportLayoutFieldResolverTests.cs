using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Validacao de campos/joins do layout contra o catalogo real de datasets.
/// Cobre a regressao do wizard: um layout multi-source sem sourceKey ou com
/// campo inexistente era aceito e so falhava (ou saia vazio) na geracao.
/// </summary>
[TestFixture]
public class ReportLayoutFieldResolverTests
{
    private static readonly IReadOnlyList<ReportDatasetCatalogItemDto> Catalog =
        new ReportDatasetCatalogProvider().GetAll();

    private static ReportLayoutDefinition Layout(params ReportLayoutDataSourceDefinition[] sources)
        => new() { DataSources = [.. sources] };

    private static ReportLayoutDataSourceDefinition Source(
        ReportDatasetType type, string alias, ReportLayoutDataSourceJoinDefinition? join = null)
        => new() { DatasetType = type, Alias = alias, Join = join };

    [Test]
    public void FieldResolver_AcceptsValidHardwareSensorsLayout()
    {
        var layout = new ReportLayoutDefinition
        {
            DataSources =
            [
                Source(ReportDatasetType.AgentHardware, "hw"),
                Source(ReportDatasetType.AgentSensors, "sns", new ReportLayoutDataSourceJoinDefinition
                {
                    JoinToAlias = "hw",
                    SourceKey = "agentId",
                    TargetKey = "agentId",
                    JoinType = "left"
                })
            ],
            Columns =
            [
                new ReportLayoutColumnDefinition { Field = "hw.agentHostname" },
                new ReportLayoutColumnDefinition { Field = "hw.totalMemoryGB" }
            ],
            GroupBy = "hw.agentHostname",
            Sections =
            [
                new ReportLayoutSectionDefinition
                {
                    Title = "Sensores",
                    Columns = [new ReportLayoutColumnDefinition { Field = "sns.sensorName" }]
                }
            ],
            Summaries = [new ReportLayoutSummaryDefinition { Aggregate = "count" }]
        };

        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
    }

    [Test]
    public void FieldResolver_RejectsUnknownField()
    {
        var layout = new ReportLayoutDefinition
        {
            DataSources =
            [
                Source(ReportDatasetType.AgentHardware, "hw"),
                Source(ReportDatasetType.AgentSensors, "sns", new ReportLayoutDataSourceJoinDefinition
                {
                    JoinToAlias = "hw",
                    SourceKey = "agentId",
                    TargetKey = "agentId",
                    JoinType = "left"
                })
            ],
            Columns = [new ReportLayoutColumnDefinition { Field = "hw.agentIde" }]
        };

        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        Assert.That(errors, Is.Not.Empty);
        Assert.That(string.Join(" ", errors), Does.Contain("hw.agentIde"));
        Assert.That(string.Join(" ", errors), Does.Contain("columns[0].field"));
    }

    [Test]
    public void FieldResolver_RejectsEmptyJoinKey()
    {
        var layout = new ReportLayoutDefinition
        {
            DataSources =
            [
                Source(ReportDatasetType.AgentHardware, "hw"),
                Source(ReportDatasetType.AgentSensors, "sns", new ReportLayoutDataSourceJoinDefinition
                {
                    JoinToAlias = "hw",
                    SourceKey = "   ",
                    TargetKey = "agentId",
                    JoinType = "left"
                })
            ],
            Columns = [new ReportLayoutColumnDefinition { Field = "hw.agentHostname" }]
        };

        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        Assert.That(errors, Is.Not.Empty, "sourceKey vazio precisa ser erro explicito");
        var joined = string.Join(" ", errors);
        Assert.That(joined, Does.Contain("dataSources[1].join.sourceKey"));
    }

    [Test]
    public void FieldResolver_RejectsJoinKeyMissingOnTarget()
    {
        var layout = Layout(
            Source(ReportDatasetType.AgentHardware, "hw"),
            Source(ReportDatasetType.AgentSensors, "sns", new ReportLayoutDataSourceJoinDefinition
            {
                JoinToAlias = "hw",
                SourceKey = "agentId",
                TargetKey = "sensorName", // existe no sns, nao no hw
                JoinType = "left"
            }));

        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        var joined = string.Join(" ", errors);
        Assert.That(joined, Does.Contain("dataSources[1].join.targetKey"));
        Assert.That(joined, Does.Contain("hw"));
    }

    [Test]
    public void FieldResolver_RejectsJoinToUnknownAlias()
    {
        var layout = Layout(
            Source(ReportDatasetType.AgentHardware, "hw"),
            Source(ReportDatasetType.AgentSensors, "sns", new ReportLayoutDataSourceJoinDefinition
            {
                JoinToAlias = "naoExiste",
                SourceKey = "agentId",
                TargetKey = "agentId",
                JoinType = "left"
            }));

        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        Assert.That(string.Join(" ", errors), Does.Contain("naoExiste"));
    }

    [Test]
    public void FieldResolver_AcceptsPlainFieldNameOnSingleSource()
    {
        var layout = new ReportLayoutDefinition
        {
            DataSources = [Source(ReportDatasetType.AgentHardware, "hw")],
            Columns =
            [
                new ReportLayoutColumnDefinition { Field = "agentHostname" },
                new ReportLayoutColumnDefinition { Field = "hw.osName" }
            ]
        };

        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
    }

    [Test]
    public void FieldResolver_RejectsFieldOfDatasetNotDeclaredInMultiSource()
    {
        // Layout multi-source declara hw+sns, mas usa "wu.kbId" (windowsUpdates).
        var layout = new ReportLayoutDefinition
        {
            DataSources =
            [
                Source(ReportDatasetType.AgentHardware, "hw"),
                Source(ReportDatasetType.AgentSensors, "sns", new ReportLayoutDataSourceJoinDefinition
                {
                    JoinToAlias = "hw",
                    SourceKey = "agentId",
                    TargetKey = "agentId",
                    JoinType = "left"
                })
            ],
            Columns = [new ReportLayoutColumnDefinition { Field = "wu.kbId" }]
        };

        var errors = ReportLayoutFieldResolver.Resolve(layout, Catalog);

        Assert.That(string.Join(" ", errors), Does.Contain("wu.kbId"));
    }

    [Test]
    public void FieldResolver_IgnoresEmptyLayout()
    {
        var errors = ReportLayoutFieldResolver.Resolve(ReportLayoutDefinition.Empty, Catalog);
        Assert.That(errors, Is.Empty);
    }
}
