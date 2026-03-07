namespace Meduza.Core.Configuration;

public class ReportingOptions
{
    /// <summary>
    /// Enable PDF export format using Playwright.NET (embedded, zero vulnerabilities).
    /// When enabled, PDF rendering runs within the same API process using headless Chromium.
    /// </summary>
    public bool EnablePdf { get; set; } = false;
}
