namespace Discovery.Core.Configuration;

public class ReportingOptions
{
    /// <summary>
    /// Enable PDF export format using Playwright.NET (embedded, zero vulnerabilities).
    /// When enabled, PDF rendering runs within the same API process using headless Chromium.
    /// </summary>
    public bool EnablePdf { get; set; } = false;

    /// <summary>
    /// Timeout in seconds for report processing (data fetch + rendering + file write).
    /// Default: 300 seconds (5 minutes).
    /// </summary>
    public int ProcessingTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Timeout in seconds for file download operations.
    /// Default: 30 seconds.
    /// </summary>
    public int FileDownloadTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of concurrent report executions when processing pending queue.
    /// Values less than 1 are normalized to 1.
    /// </summary>
    public int MaxConcurrentExecutions { get; set; } = 2;

    /// <summary>
    /// Retention period in days for report execution rows in database.
    /// Allowed values should match AllowedRetentionDays.
    /// </summary>
    public int DatabaseRetentionDays { get; set; } = 90;

    /// <summary>
    /// Retention period in days for generated report files on disk.
    /// Allowed values should match AllowedRetentionDays.
    /// </summary>
    public int FileRetentionDays { get; set; } = 90;

    /// <summary>
    /// Allowed retention values for reports.
    /// </summary>
    public int[] AllowedRetentionDays { get; set; } = [30, 60, 90];
}
