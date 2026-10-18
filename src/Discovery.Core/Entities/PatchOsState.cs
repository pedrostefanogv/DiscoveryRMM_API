namespace Discovery.Core.Entities;

/// <summary>
/// Auditoria de enforcement no SO: guarda o estado anterior da política de
/// Windows Update para permitir rollback fiel (best-effort, opt-in).
/// </summary>
public class PatchOsState
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid? CommandId { get; set; }
    public string? AppliedBy { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Snapshot JSON dos valores anteriores (Win32/Policies).</summary>
    public string? PreviousJson { get; set; }
    /// <summary>Valores aplicados (JSON).</summary>
    public string? AppliedJson { get; set; }
    public DateTime? RevertedAt { get; set; }
    public string? RevertedBy { get; set; }
}
