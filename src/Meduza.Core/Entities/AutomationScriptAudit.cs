using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class AutomationScriptAudit
{
    public Guid Id { get; set; }
    public Guid ScriptId { get; set; }
    public AutomationScriptChangeType ChangeType { get; set; }
    public string? Reason { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string? ChangedBy { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
