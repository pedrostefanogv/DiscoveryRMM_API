using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AppNotification
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Informational;
    public Guid? RecipientUserId { get; set; }
    public Guid? RecipientAgentId { get; set; }
    public string? RecipientKey { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? CreatedBy { get; set; }
}
