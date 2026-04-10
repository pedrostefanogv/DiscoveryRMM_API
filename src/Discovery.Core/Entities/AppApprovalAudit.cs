using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AppApprovalAudit
{
    public Guid Id { get; set; }
    public Guid? RuleId { get; set; }
    public AppApprovalAuditChangeType ChangeType { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public AppInstallationType InstallationType { get; set; } = AppInstallationType.Winget;
    public string PackageId { get; set; } = string.Empty;
    public AppApprovalActionType? OldAction { get; set; }
    public AppApprovalActionType? NewAction { get; set; }
    public bool? OldAutoUpdateEnabled { get; set; }
    public bool? NewAutoUpdateEnabled { get; set; }
    public string? Reason { get; set; }
    public string? ChangedBy { get; set; }
    public string? IpAddress { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
