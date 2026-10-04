using System.Text.Json;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// O validador aceita 13 agregacoes; o calculo precisa entregar todas, senao
/// "median"/"percentile90"/"first"/"last" renderizam "-" silenciosamente.
/// </summary>
public class ReportAggregateCalculatorTests
{
    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows =
    [
        new Dictionary<string, object?> { ["value"] = 10m, ["active"] = true },
        new Dictionary<string, object?> { ["value"] = 20m, ["active"] = false },
        new Dictionary<string, object?> { ["value"] = 30m, ["active"] = true },
        new Dictionary<string, object?> { ["value"] = 40m, ["active"] = true }
    ];

    [Test]
    public void Median_EvenCount_AveragesMiddleValues()
        => Assert.That(ReportAggregateCalculator.Compute("median", "value", null, Rows), Is.EqualTo(25m));

    [Test]
    public void Percentile90_ReturnsNearestRank()
        => Assert.That(ReportAggregateCalculator.Compute("percentile90", "value", null, Rows), Is.EqualTo(40m));

    [Test]
    public void FirstAndLast_ReturnBoundaryValues()
    {
        Assert.That(ReportAggregateCalculator.Compute("first", "value", null, Rows), Is.EqualTo(10m));
        Assert.That(ReportAggregateCalculator.Compute("last", "value", null, Rows), Is.EqualTo(40m));
    }

    [Test]
    public void CountIf_WithoutCondition_CountsTrueBooleans()
        => Assert.That(ReportAggregateCalculator.Compute("countIf", "active", null, Rows), Is.EqualTo(3));

    [Test]
    public void Sum_AveragesAndDistinct_StillWork()
    {
        Assert.That(ReportAggregateCalculator.Compute("sum", "value", null, Rows), Is.EqualTo(100m));
        Assert.That(ReportAggregateCalculator.Compute("avg", "value", null, Rows), Is.EqualTo(25m));
        Assert.That(ReportAggregateCalculator.Compute("count", null, null, Rows), Is.EqualTo(4));
    }

    [Test]
    public void UnknownAggregate_ReturnsNull()
        => Assert.That(ReportAggregateCalculator.Compute("naoExiste", "value", null, Rows), Is.Null);

    [Test]
    public void CountIf_WithEqCondition_FiltersRows()
    {
        using var document = JsonDocument.Parse("""{ "eq": true }""");
        var result = ReportAggregateCalculator.Compute("countIf", "active", document.RootElement, Rows);
        Assert.That(result, Is.EqualTo(3));
    }
}
