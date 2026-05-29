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
    public void HtmlComposer_WhenGroupedRowsHaveJoinFanOut_DeduplicatesMainTableRows()
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
                { "field": "totalMemoryGB", "header": "RAM (GB)" },
                { "field": "osName", "header": "SO" },
                { "field": "processor", "header": "Processador" }
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
            Columns = ["agentHostname", "totalMemoryGB", "osName", "processor", "softwareName", "version"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "AORUSAXV2",
                    ["totalMemoryGB"] = 64,
                    ["osName"] = "Windows 11",
                    ["processor"] = "Ryzen 7",
                    ["softwareName"] = "Chrome",
                    ["version"] = "126"
                },
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "AORUSAXV2",
                    ["totalMemoryGB"] = 64,
                    ["osName"] = "Windows 11",
                    ["processor"] = "Ryzen 7",
                    ["softwareName"] = "Firefox",
                    ["version"] = "127"
                }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(CountOccurrences(html, "<table>"), Is.EqualTo(2));
        Assert.That(CountOccurrences(html, "<td>64</td><td>Windows 11</td><td>Ryzen 7</td>"), Is.EqualTo(1));
        Assert.That(CountOccurrences(html, "<td>Chrome</td><td>126</td>"), Is.EqualTo(1));
        Assert.That(CountOccurrences(html, "<td>Firefox</td><td>127</td>"), Is.EqualTo(1));
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

    [Test]
    public async Task MarkdownRenderer_WhenGroupedRowsHaveJoinFanOut_DeduplicatesMainTableRows()
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
                { "field": "totalMemoryGB", "header": "RAM (GB)" },
                { "field": "osName", "header": "SO" },
                { "field": "processor", "header": "Processador" }
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
            Columns = ["agentHostname", "totalMemoryGB", "osName", "processor", "softwareName", "version"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "AORUSAXV2",
                    ["totalMemoryGB"] = 64,
                    ["osName"] = "Windows 11",
                    ["processor"] = "Ryzen 7",
                    ["softwareName"] = "Chrome",
                    ["version"] = "126"
                },
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "AORUSAXV2",
                    ["totalMemoryGB"] = 64,
                    ["osName"] = "Windows 11",
                    ["processor"] = "Ryzen 7",
                    ["softwareName"] = "Firefox",
                    ["version"] = "127"
                }
            ]
        };

        var result = await renderer.RenderAsync(context, data);
        var markdown = Encoding.UTF8.GetString(result.Content);

        Assert.That(CountOccurrences(markdown, "| 64 | Windows 11 | Ryzen 7 |"), Is.EqualTo(1));
        Assert.That(CountOccurrences(markdown, "| Chrome | 126 |"), Is.EqualTo(1));
        Assert.That(CountOccurrences(markdown, "| Firefox | 127 |"), Is.EqualTo(1));
    }

    [Test]
    public void HtmlComposer_WhenRowsExceedPageThreshold_InsertsPageBreakMarkers()
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
              ]
            }
            """
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 1; i <= 70; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["agentHostname"] = $"PC-{i:D3}",
                ["osName"] = "Windows"
            });
        }

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "osName"],
            Rows = rows
        };

        var html = composer.Compose(context, data);

        Assert.That(CountOccurrences(html, "<table>"), Is.EqualTo(3));
        Assert.That(CountOccurrences(html, "<div class=\"report-page-break\"></div>"), Is.EqualTo(2));
        Assert.That(html, Does.Contain("Pagina 2/3"));
        Assert.That(html, Does.Contain("<td>PC-070</td>"));
    }

    [Test]
    public async Task MarkdownRenderer_WhenRowsExceedPageThreshold_InsertsPageBreakMarkers()
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
              ]
            }
            """
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 1; i <= 70; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["agentHostname"] = $"PC-{i:D3}",
                ["osName"] = "Windows"
            });
        }

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "osName"],
            Rows = rows
        };

        var result = await renderer.RenderAsync(context, data);
        var markdown = Encoding.UTF8.GetString(result.Content);

        Assert.That(markdown, Does.Contain("_Pagina 1 de 3_"));
        Assert.That(markdown, Does.Contain("_Pagina 3 de 3_"));
        Assert.That(CountOccurrences(markdown, "<div style=\"page-break-after: always;\"></div>"), Is.EqualTo(2));
        Assert.That(markdown, Does.Contain("| PC-070 | Windows |"));
    }

    [Test]
    public void HtmlComposer_WhenWatermarkUsesLogo_RendersImageWatermark()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "logoUrl": "https://cdn.exemplo.local/logo.png",
              "watermark": {
                "useLogo": true,
                "imageFit": "cover",
                "imageOpacity": 0.12
              },
              "columns": [
                { "field": "agentHostname", "header": "Hostname" }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01"
                }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Contain("report-watermark-image-cover"));
        Assert.That(html, Does.Contain("src=\"https://cdn.exemplo.local/logo.png\""));
        Assert.That(html, Does.Contain("style=\"opacity:0.12;\""));
    }

    [Test]
    public void HtmlComposer_WhenShowRowStripesIsDisabled_UsesTransparentAlternateRowColor()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "style": {
                "alternateRowColor": "#EEF4F7",
                "showRowStripes": false
              },
              "columns": [
                { "field": "agentHostname", "header": "Hostname" }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01"
                }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Contain("--report-alt-row: transparent;"));
    }

    [Test]
    public void HtmlComposer_WhenWatermarkHasDedicatedLogoUrl_PrioritizesDedicatedUrl()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "logoUrl": "https://cdn.exemplo.local/logo-principal.png",
              "watermark": {
                "useLogo": true,
                "logoUrl": "https://cdn.exemplo.local/logo-watermark.png",
                "imageOpacity": 0.2
              },
              "columns": [
                { "field": "agentHostname", "header": "Hostname" }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01"
                }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Contain("src=\"https://cdn.exemplo.local/logo-principal.png\""));
        Assert.That(html, Does.Contain("src=\"https://cdn.exemplo.local/logo-watermark.png\""));
    }

    [Test]
    public void HtmlComposer_WhenRenderingAnyReport_IncludesGeneratedFooterMetadata()
    {
        var composer = new ReportHtmlComposer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "columns": [
                { "field": "agentHostname", "header": "Hostname" }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01"
                }
            ]
        };

        var html = composer.Compose(context, data);

        Assert.That(html, Does.Contain("Gerado por Discovery RMM em"));
        Assert.That(html, Does.Contain("1 registro(s)"));
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
