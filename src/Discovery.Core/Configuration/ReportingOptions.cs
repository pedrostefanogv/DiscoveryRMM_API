namespace Discovery.Core.Configuration;

public class ReportingOptions
{
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
    /// Retention period in days for report template history snapshots.
    /// Default: 365 days. The history table grows without a purge job.
    /// </summary>
    public int TemplateHistoryRetentionDays { get; set; } = 365;

    /// <summary>
    /// Allowed retention values for reports.
    /// </summary>
    public int[] AllowedRetentionDays { get; set; } = [30, 60, 90];
}
