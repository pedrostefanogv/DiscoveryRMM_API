using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

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

    // ============ Hash chain (M241) ============

    /// <summary>Hash da entrada anterior da cadeia deste escopo (null = entrada fora da chain/legado)</summary>
    public string? PrevHash { get; set; }

    /// <summary>SHA256(prev_head || payload) em hex minúsculas; null = fail-open</summary>
    public string? EntryHash { get; set; }
}
