using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.Playwright;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// PDF renderer using Playwright.NET (headless Chromium).
/// Runs entirely within the API process - no external service required.
/// Zero known vulnerabilities (MIT license, actively maintained).
/// </summary>
public class PlaywrightPdfReportRenderer : IReportRenderer
{
    private static IBrowser? _browser;
    private static readonly SemaphoreSlim _browserLock = new(1, 1);

    public ReportFormat Format => ReportFormat.Pdf;

    public async Task<ReportDocument> RenderAsync(string title, ReportQueryResult data, CancellationToken cancellationToken = default)
    {
        try
        {
            // Lazy-init browser (singleton per app lifetime)
            if (_browser is null)
            {
                await _browserLock.WaitAsync(cancellationToken);
                try
                {
                    if (_browser is null)
                    {
                        var playwright = await Playwright.CreateAsync();
                        _browser = await playwright.Chromium.LaunchAsync();
                    }
                }
                finally
                {
                    _browserLock.Release();
                }
            }

            // Create a new context and page for this request
            var context = await _browser.NewContextAsync();
            try
            {
                var page = await context.NewPageAsync();
                try
                {
                    // Build HTML table
                    var html = BuildHtmlTable(title, data);

                    // Load HTML and render PDF
                    await page.SetContentAsync(html);
                    var pdfBytes = await page.PdfAsync(new PagePdfOptions
                    {
                        Format = "A4",
                        Margin = new()
                        {
                            Top = "20mm",
                            Right = "15mm",
                            Bottom = "20mm",
                            Left = "15mm"
                        }
                    });

                    return new ReportDocument
                    {
                        Content = pdfBytes,
                        ContentType = "application/pdf",
                        FileExtension = "pdf"
                    };
                }
                finally
                {
                    await page.CloseAsync();
                }
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"PDF rendering failed: {ex.Message}", ex);
        }
    }

    private string BuildHtmlTable(string title, ReportQueryResult data)
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <style>
                    body { font-family: Arial, sans-serif; margin: 10px; color: #333; }
                    h1 { color: #0066cc; border-bottom: 2px solid #0066cc; padding-bottom: 10px; }
                    table { width: 100%; border-collapse: collapse; margin-top: 20px; }
                    th { 
                        background-color: #0066cc; 
                        color: white; 
                        padding: 12px; 
                        text-align: left; 
                        border: 1px solid #004499;
                        font-weight: bold;
                    }
                    td { 
                        padding: 10px; 
                        border: 1px solid #ddd; 
                        text-align: left;
                    }
                    tr:nth-child(even) { background-color: #f9f9f9; }
                    tr:hover { background-color: #f0f0f0; }
                </style>
            </head>
            <body>
                <h1>{TITLE}</h1>
                <table>
                    <thead>
                        <tr>{HEADERS}</tr>
                    </thead>
                    <tbody>
                        {ROWS}
                    </tbody>
                </table>
            </body>
            </html>
            """;

        // Build headers
        var headers = string.Join("", data.Columns.Select(col => $"<th>{HtmlEscape(col)}</th>"));

        // Build rows - convert IReadOnlyList<IReadOnlyDictionary> to HTML table rows
        var rowsHtml = string.Join("\n", data.Rows.Select(row =>
        {
            var cells = data.Columns.Select(col =>
            {
                var value = row.ContainsKey(col) ? row[col] : null;
                return $"<td>{HtmlEscape(value?.ToString() ?? "")}</td>";
            });
            return $"<tr>{string.Join("", cells)}</tr>";
        }));

        return html
            .Replace("{TITLE}", HtmlEscape(title))
            .Replace("{HEADERS}", headers)
            .Replace("{ROWS}", rowsHtml);
    }

    private string HtmlEscape(string? text) =>
        System.Net.WebUtility.HtmlEncode(text ?? "");
}
