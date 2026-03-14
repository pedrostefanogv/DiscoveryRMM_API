using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class AutomationTaskAudit
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public AutomationTaskChangeType ChangeType { get; set; }
    public string? Reason { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string? ChangedBy { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
