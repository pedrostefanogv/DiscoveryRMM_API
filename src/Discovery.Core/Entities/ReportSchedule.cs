using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Represents a scheduled recurring report generation.
/// Uses cron expressions for flexible scheduling.
/// Future: email/webhook delivery will be added.
/// </summary>
public class ReportSchedule
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public Guid? ClientId { get; set; }
    public ReportFormat Format { get; set; } = ReportFormat.Pdf;
    public string? FiltersJson { get; set; }
    public string? ScheduleLabel { get; set; }

    /// <summary>Cron expression (standard 5/6/7 field). Example: "0 8 * * 1" = every Monday 08:00</summary>
    public string CronExpression { get; set; } = "0 8 * * 1";

    /// <summary>Timezone for cron evaluation. Default: UTC.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Maximum number of retained executions for this schedule. Older ones are cleaned by retention job.</summary>
    public int MaxRetainedExecutions { get; set; } = 10;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Last time this schedule was triggered (for monitoring).</summary>
    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>Next scheduled trigger time (computed on save).</summary>
    public DateTime? NextTriggerAt { get; set; }

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Delivery mode: storage (default), email, webhook.</summary>
    public string DeliveryMode { get; set; } = "storage";

    /// <summary>Comma-separated recipient emails (for email delivery).</summary>
    public string? Recipients { get; set; }

    /// <summary>URL for webhook delivery (POST with download link).</summary>
    public string? WebhookUrl { get; set; }
}
