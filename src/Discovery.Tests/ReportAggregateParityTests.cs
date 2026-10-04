using Discovery.Core.Helpers;

namespace Discovery.Tests;

public class ReportAggregateParityTests
{
    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows =
    [
        new Dictionary<string, object?> { ["value"] = 10m }
    ];

    [Test]
    public void Compute_WithoutField_ReturnsNullForNonCountAggregates()
    {
        Assert.That(ReportAggregateCalculator.Compute("sum", null, null, Rows), Is.Null);
        Assert.That(ReportAggregateCalculator.Compute("avg", "", null, Rows), Is.Null);
        Assert.That(ReportAggregateCalculator.Compute("compliancePercent", null, null, Rows), Is.Null);
        Assert.That(ReportAggregateCalculator.Compute("median", null, null, Rows), Is.Null);
    }

    [Test]
    public void Compute_Count_DoesNotRequireField()
        => Assert.That(ReportAggregateCalculator.Compute("count", null, null, Rows), Is.EqualTo(1));

    [Test]
    public void Compute_SkipsNonNumericStrings()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["value"] = "10" },
            new Dictionary<string, object?> { ["value"] = 5m }
        };
        Assert.That(ReportAggregateCalculator.Compute("sum", "value", null, rows), Is.EqualTo(5m));
    }
}
