using System.Text;
using ClosedXML.Excel;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

public class ReportRendererColumnProjectionTests
{
    [Test]
    public async Task CsvRenderer_WhenLayoutDefinesColumns_ExportsOnlyConfiguredColumns()
    {
        var renderer = new CsvReportRenderer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "columns": [
                { "field": "agentHostname", "label": "Agente" },
                { "field": "osName", "label": "Sistema Operacional" }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "osName", "softwareName", "publisher"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01",
                    ["osName"] = "Windows 11",
                    ["softwareName"] = "Chrome",
                    ["publisher"] = "Google"
                }
            ]
        };

        var result = await renderer.RenderAsync(context, data);
        var content = Encoding.UTF8.GetString(result.Content);
        var lines = content
          .Split('\n', StringSplitOptions.RemoveEmptyEntries)
          .Select(line => line.TrimEnd('\r'))
          .ToArray();

        Assert.That(lines.Length, Is.GreaterThanOrEqualTo(3));
        Assert.That(lines[1], Is.EqualTo("\"Agente\",\"Sistema Operacional\""));
        Assert.That(lines[2], Is.EqualTo("\"PC-01\",\"Windows 11\""));
        Assert.That(content, Does.Not.Contain("softwareName"));
        Assert.That(content, Does.Not.Contain("publisher"));
    }

    [Test]
    public async Task XlsxRenderer_WhenLayoutUsesSections_ExportsOnlySectionColumns()
    {
        var renderer = new XlsxReportRenderer();
        var context = new ReportRenderContext
        {
            TemplateName = "Preview",
            LayoutJson = """
            {
              "sections": [
                {
                  "title": "Hardware",
                  "columns": [
                    { "field": "agentHostname", "label": "Agente" },
                    { "field": "totalMemoryGB", "label": "Memoria (GB)" }
                  ]
                }
              ]
            }
            """
        };

        var data = new ReportQueryResult
        {
            Columns = ["agentHostname", "totalMemoryGB", "softwareName"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["agentHostname"] = "PC-01",
                    ["totalMemoryGB"] = 16,
                    ["softwareName"] = "Chrome"
                }
            ]
        };

        var result = await renderer.RenderAsync(context, data);

        using var stream = new MemoryStream(result.Content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Report");

        Assert.That(sheet.Cell(3, 1).GetString(), Is.EqualTo("Agente"));
        Assert.That(sheet.Cell(3, 2).GetString(), Is.EqualTo("Memoria (GB)"));
        Assert.That(sheet.Cell(3, 3).IsEmpty(), Is.True);

        Assert.That(sheet.Cell(4, 1).GetString(), Is.EqualTo("PC-01"));
        Assert.That(sheet.Cell(4, 2).GetString(), Is.EqualTo("16"));
    }
}
