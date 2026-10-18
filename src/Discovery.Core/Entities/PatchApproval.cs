using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Aprovação/recusa explícita de uma atualização, por escopo (Global → Agent).
/// A chave é o <c>GroupKey</c> do inventário (KB quando existe).
/// </summary>
public class PatchApproval
{
    public Guid Id { get; set; }
    public PatchApprovalScopeType ScopeType { get; set; }
    /// <summary>Id do escopo (null quando Global).</summary>
    public Guid? ScopeId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    public string UpdateKey { get; set; } = string.Empty;
    public string? KbId { get; set; }
    public PatchApprovalActionType Action { get; set; } = PatchApprovalActionType.Approve;
    public string? Reason { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
