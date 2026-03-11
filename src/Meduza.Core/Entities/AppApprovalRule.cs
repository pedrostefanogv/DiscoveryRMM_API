using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class AppApprovalRule
{
    public Guid Id { get; set; }

    public AppApprovalScopeType ScopeType { get; set; } = AppApprovalScopeType.Global;
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    public AppInstallationType InstallationType { get; set; } = AppInstallationType.Winget;
    public string PackageId { get; set; } = string.Empty;
    public AppApprovalActionType Action { get; set; } = AppApprovalActionType.Allow;
    public bool? AutoUpdateEnabled { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
