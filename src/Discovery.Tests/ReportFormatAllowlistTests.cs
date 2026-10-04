using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Formatos suportados pelos renderers precisam estar na allowlist do validador:
/// "bytes" e "percent" faltavam e reprovavam os proprios templates de fabrica.
/// 12 colunas do M113 usam format "bytes".
/// </summary>
public class ReportFormatAllowlistTests
{
    private const string LayoutWithBytesAndPercent = """
    {
      "title": "Hardware",
      "columns": [
        { "field": "totalMemoryBytes", "header": "RAM", "format": "bytes" },
        { "field": "usedPercent", "header": "Uso", "format": "percent" },
        { "field": "collectedAt", "header": "Coleta", "format": "datetime" }
      ],
      "summaries": [
        { "label": "RAM Total", "field": "totalMemoryBytes", "aggregate": "sum", "format": "bytes" },
        { "label": "Uso Medio", "field": "usedPercent", "aggregate": "avg", "format": "percent" }
      ]
    }
    """;

    [Test]
    public void ValidateJson_WhenFormatsAreBytesOrPercent_IsValid()
    {
        var errors = ReportLayoutValidator.ValidateJson(LayoutWithBytesAndPercent);
        Assert.That(errors, Is.Empty, "erros: " + string.Join(" | ", errors));
    }

    [Test]
    public void GetSupportedColumnFormats_IncludesBytesAndPercent()
    {
        var formats = ReportLayoutValidator.GetSupportedColumnFormats();
        Assert.That(formats, Does.Contain("bytes"));
        Assert.That(formats, Does.Contain("percent"));
    }
}
