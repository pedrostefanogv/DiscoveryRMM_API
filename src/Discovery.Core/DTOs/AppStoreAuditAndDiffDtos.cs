using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class AppApprovalAuditPageDto
{
    public AppInstallationType InstallationType { get; set; }
    public string? PackageId { get; set; }
    public AppApprovalScopeType? ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime? ChangedFrom { get; set; }
    public DateTime? ChangedTo { get; set; }
    public AppApprovalAuditChangeType? ChangeType { get; set; }
    public int ReturnedItems { get; set; }
    public Guid? Cursor { get; set; }
    public Guid? NextCursor { get; set; }
    public int Limit { get; set; }
    public bool HasMore { get; set; }
    public IReadOnlyList<AppApprovalAuditEntryDto> Items { get; set; } = [];
}

public class AppApprovalAuditEntryDto
{
    public Guid Id { get; set; }
    public Guid? RuleId { get; set; }
    public AppApprovalAuditChangeType ChangeType { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public AppApprovalActionType? OldAction { get; set; }
    public AppApprovalActionType? NewAction { get; set; }
    public bool? OldAutoUpdateEnabled { get; set; }
    public bool? NewAutoUpdateEnabled { get; set; }
    public string? Reason { get; set; }
    public string? ChangedBy { get; set; }
    public string? IpAddress { get; set; }
    public DateTime ChangedAt { get; set; }
}

public class AppApprovalDiffLevelDto
{
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppApprovalActionType? Action { get; set; }
    public bool? AutoUpdateEnabled { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool AppliedToEffectiveResult { get; set; }
    public string? Outcome { get; set; }
    public string? Reason { get; set; }
}

public class AppApprovalPackageDiffDto
{
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public bool AutoUpdateEnabled { get; set; }
    public AppApprovalScopeType? EffectiveSourceScope { get; set; }
    public string? EffectiveReason { get; set; }
    public IReadOnlyList<AppApprovalDiffLevelDto> Levels { get; set; } = [];
}

public class AppEffectivePackageDiffPageDto
{
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string? Search { get; set; }
    public int ReturnedItems { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public int Limit { get; set; }
    public bool HasMore { get; set; }
    public IReadOnlyList<AppApprovalPackageDiffDto> Items { get; set; } = [];
}
