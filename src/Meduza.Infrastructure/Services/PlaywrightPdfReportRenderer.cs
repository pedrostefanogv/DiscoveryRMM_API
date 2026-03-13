using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.Playwright;
using System.Text;

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
    private readonly IReportHtmlComposer _htmlComposer;

    public PlaywrightPdfReportRenderer(IReportHtmlComposer htmlComposer)
    {
        _htmlComposer = htmlComposer;
    }

    public ReportFormat Format => ReportFormat.Pdf;

    public async Task<ReportDocument> RenderAsync(ReportRenderContext context, ReportQueryResult data, CancellationToken cancellationToken = default)
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
            var browserContext = await _browser.NewContextAsync();
            try
            {
                var page = await browserContext.NewPageAsync();
                try
                {
                    // Build HTML table
                    var html = _htmlComposer.Compose(context, data);
                    var layout = ReportLayoutDefinitionParser.ParseOrDefault(context.LayoutJson);

                    // Load HTML and render PDF
                    await page.SetContentAsync(html);
                    var pdfBytes = await page.PdfAsync(new PagePdfOptions
                    {
                        Format = "A4",
                        Landscape = string.Equals(layout.Orientation, "landscape", StringComparison.OrdinalIgnoreCase),
                        PrintBackground = true,
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
                await browserContext.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"PDF rendering failed: {ex.Message}", ex);
        }
    }

}
