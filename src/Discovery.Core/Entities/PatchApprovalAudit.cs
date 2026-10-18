using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>Histórico imutável de mudanças em aprovações de patch.</summary>
public class PatchApprovalAudit
{
    public Guid Id { get; set; }
    public Guid? RuleId { get; set; }
    public PatchApprovalAuditChangeType ChangeType { get; set; }
    public PatchApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public string UpdateKey { get; set; } = string.Empty;
    public string? KbId { get; set; }
    public PatchApprovalActionType? OldAction { get; set; }
    public PatchApprovalActionType? NewAction { get; set; }
    public string? Reason { get; set; }
    public string? ChangedBy { get; set; }
    public string? IpAddress { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
