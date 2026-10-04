using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Os graficos do relatorio precisam ser auto-contidos: antes eram imagens
/// apontando para https://quickchart.io, o que enviava dados do cliente a um
/// terceiro a cada visualizacao e nao funcionava offline/air-gapped.
/// </summary>
public class ReportChartRenderingTests
{
    [Test]
    public void HtmlComposer_WhenLayoutHasCharts_RendersInlineSvgWithoutExternalService()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "columns": [ { "field": "osName", "header": "SO" } ],
              "charts": [
                { "type": "pie", "title": "Distribuicao", "groupField": "osName", "aggregate": "count" },
                { "type": "bar", "title": "Por SO", "groupField": "osName", "aggregate": "count" },
                { "type": "horizontalBar", "title": "Top", "groupField": "osName", "aggregate": "count" },
                { "type": "line", "title": "Linha", "groupField": "osName", "aggregate": "count" },
                { "type": "gauge", "title": "Conformidade", "aggregate": "compliancePercent", "groupField": "osName" }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["osName"],
            Rows =
            [
                new Dictionary<string, object?> { ["osName"] = "Windows 11" },
                new Dictionary<string, object?> { ["osName"] = "Linux" },
                new Dictionary<string, object?> { ["osName"] = "Windows 11" }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Contain("<svg"), "os graficos devem ser SVG inline");
        Assert.That(html, Does.Not.Contain("quickchart.io"), "nao pode depender de servico externo de graficos");
        Assert.That(html, Does.Not.Contain("src=\"http"), "nao pode emitir imagem remota");
        Assert.That(html, Does.Not.Contain("<img class=\"report-chart\""), "graficos nao devem ser <img>");
        Assert.That(html, Does.Contain("</svg>"));
    }
}
