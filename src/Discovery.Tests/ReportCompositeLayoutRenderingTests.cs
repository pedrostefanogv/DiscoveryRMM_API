using System.Text;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

public class ReportCompositeLayoutRenderingTests
{
    [Test]
    public void HtmlComposer_WhenLayoutHasColumnsAndSections_RendersMainAndSectionTables()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "osName", "header": "SO" }
              ],
              "sections": [
                {
                  "title": "Software",
                  "columns": [
                    { "field": "softwareName", "header": "Software" },
                    { "field": "version", "header": "Versao" }
                  ]
                }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "osName", "softwareName", "version"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01",
                    ["osName"] = "Windows 11",
                    ["softwareName"] = "Chrome",
                    ["version"] = "126"
                }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(CountOccurrences(html, "<table>"), Is.EqualTo(2));
        Assert.That(html, Does.Contain("<th>Hostname</th>"));
        Assert.That(html, Does.Contain("<th>SO</th>"));
        Assert.That(html, Does.Contain("<th>Software</th>"));
        Assert.That(html, Does.Contain("<th>Versao</th>"));
        Assert.That(html, Does.Contain("<td>Windows 11</td>"));
    }

    [Test]
    public void HtmlComposer_WhenGroupedAndHideGroupColumn_RendersMainWithoutGroupColumnAndSections()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "groupBy": "agentHostname",
              "hideGroupColumn": true,
              "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "osName", "header": "SO" }
              ],
              "sections": [
                {
                  "title": "Software",
                  "columns": [
                    { "field": "softwareName", "header": "Software" },
                    { "field": "version", "header": "Versao" }
                  ]
                }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "osName", "softwareName", "version"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01",
                    ["osName"] = "Windows 11",
                    ["softwareName"] = "Chrome",
                    ["version"] = "126"
                }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Contain("<h2 class=\"report-group-title\">PC-01</h2>"));
        Assert.That(CountOccurrences(html, "<table>"), Is.EqualTo(2));
        Assert.That(html, Does.Contain("<th>SO</th>"));
        Assert.That(html, Does.Not.Contain("<th>Hostname</th>"));
        Assert.That(html, Does.Contain("<th>Software</th>"));
    }

    [Test]
    public async Task MarkdownRenderer_WhenLayoutHasColumnsAndSections_RendersMainAndSectionTables()
    {
        var renderer = new MarkdownReportRenderer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "osName", "header": "SO" }
              ],
              "sections": [
                {
                  "title": "Software",
                  "columns": [
                    { "field": "softwareName", "header": "Software" },
                    { "field": "version", "header": "Versao" }
                  ]
                }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "osName", "softwareName", "version"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01",
                    ["osName"] = "Windows 11",
                    ["softwareName"] = "Chrome",
                    ["version"] = "126"
                }
            ]
        };

        var result = await renderer.RenderAsync(context, data);
        var markdown = Encoding.UTF8.GetString(result.Content);

        Assert.That(markdown, Does.Contain("## Dados"));
        Assert.That(markdown, Does.Contain("| Hostname | SO |"));
        Assert.That(markdown, Does.Contain("| Software | Versao |"));
        Assert.That(markdown, Does.Contain("| PC-01 | Windows 11 |"));
        Assert.That(markdown, Does.Contain("| Chrome | 126 |"));
    }

    [Test]
    public async Task MarkdownRenderer_WhenGroupedAndHideGroupColumn_RendersMainWithoutGroupColumnAndSections()
    {
        var renderer = new MarkdownReportRenderer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "groupBy": "agentHostname",
              "hideGroupColumn": true,
              "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "osName", "header": "SO" }
              ],
              "sections": [
                {
                  "title": "Software",
                  "columns": [
                    { "field": "softwareName", "header": "Software" },
                    { "field": "version", "header": "Versao" }
                  ]
                }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "osName", "softwareName", "version"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01",
                    ["osName"] = "Windows 11",
                    ["softwareName"] = "Chrome",
                    ["version"] = "126"
                }
            ]
        };

        var result = await renderer.RenderAsync(context, data);
        var markdown = Encoding.UTF8.GetString(result.Content);

        Assert.That(markdown, Does.Contain("## PC-01 (1 registro(s))"));
        Assert.That(markdown, Does.Contain("| SO |"));
        Assert.That(markdown, Does.Not.Contain("| Hostname | SO |"));
        Assert.That(markdown, Does.Contain("### Software"));
        Assert.That(markdown, Does.Contain("| Software | Versao |"));
    }

    private static int CountOccurrences(string input, string token)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(token))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = input.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
