using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressao de seguranca do composer HTML de relatorios.
///
/// O ReportLayoutValidator nao e chamado no caminho de preview/gravacao de
/// template, portanto o HTML nao pode confiar no conteudo do LayoutJson. Estas
/// checagens garantem que valores do layout sao sempre escapados antes de
/// entrar em atributos/HTML.
/// </summary>
public class ReportHtmlComposerSecurityTests
{
    [Test]
    public void HtmlComposer_WhenConditionalFormatColorBreaksOutOfAttribute_EscapesColor()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "columns": [
                {
                  "field": "hostname",
                  "header": "Host",
                  "conditionalFormat": {
                    "rules": [
                      {
                        "operator": "eq",
                        "value": "BAD",
                        "backgroundColor": "\" onmouseover=\"alert(1)"
                      }
                    ]
                  }
                }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["hostname"],
            Rows = [new Dictionary<string, object?> { ["hostname"] = "BAD" }]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Not.Contain("onmouseover=\"alert(1)\""), "o valor nao pode quebrar o atributo style");
        Assert.That(html, Does.Contain("&quot;"), "as aspas do valor devem ser escapadas");
        Assert.That(html, Does.Contain("background-color:"), "a cor legitima continua aplicada");
    }

    [Test]
    public void HtmlComposer_WhenCellValueContainsHtml_EscapesCellContent()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            { "columns": [ { "field": "softwareName", "header": "Software" } ] }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["softwareName"],
            Rows = [new Dictionary<string, object?> { ["softwareName"] = "<script>alert(1)</script>" }]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Not.Contain("<script>alert(1)</script>"));
        Assert.That(html, Does.Contain("&lt;script&gt;"));
    }
}
