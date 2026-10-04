using Discovery.Core.Helpers;
using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// Campos calculados precisam ser aplicados ANTES do render, para valerem em
/// todos os formatos (antes somente o composer HTML calculava).
/// </summary>
public class ReportComputedFieldEvaluatorTests
{
    private static ReportLayoutDefinition Layout(params (string Name, string Expression)[] fields) => new()
    {
        ComputedFields = fields
            .Select(f => new ReportLayoutComputedFieldDefinition { Name = f.Name, Expression = f.Expression })
            .ToList()
    };

    [Test]
    public void Enrich_AddsComputedColumnToEveryRow()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["usedBytes"] = 50m, ["totalBytes"] = 100m },
            new Dictionary<string, object?> { ["usedBytes"] = 25m, ["totalBytes"] = 100m }
        };

        var enriched = ReportComputedFieldEvaluator.Enrich(Layout(("freeBytes", "totalBytes - usedBytes")), rows);

        Assert.That(enriched[0]["freeBytes"], Is.EqualTo(50m));
        Assert.That(enriched[1]["freeBytes"], Is.EqualTo(75m));
    }

    [Test]
    public void Enrich_WhenNoComputedFields_ReturnsSameList()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["a"] = 1 } };
        Assert.That(ReportComputedFieldEvaluator.Enrich(new ReportLayoutDefinition(), rows), Is.SameAs(rows));
    }
}
