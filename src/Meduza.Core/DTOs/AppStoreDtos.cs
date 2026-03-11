using Meduza.Core.Enums;

namespace Meduza.Core.DTOs;

public class AppCatalogPackageDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string InstallCommand { get; set; } = string.Empty;
    public DateTime? LastUpdated { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public IReadOnlyDictionary<string, string> InstallerUrlsByArch { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public class AppCatalogSearchResultDto
{
    public DateTime? GeneratedAt { get; set; }
    public int TotalPackagesInSource { get; set; }
    public int ReturnedItems { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public int Limit { get; set; }
    public bool HasMore { get; set; }
    public string? Search { get; set; }
    public string? Architecture { get; set; }
    public IReadOnlyList<AppCatalogPackageDto> Items { get; set; } = [];
}

public class AppApprovalRuleResolvedDto
{
    public Guid RuleId { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public AppApprovalActionType Action { get; set; }
    public bool? AutoUpdateEnabled { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class EffectiveApprovedAppDto
{
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallCommand { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> InstallerUrlsByArch { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool AutoUpdateEnabled { get; set; }
    public AppApprovalScopeType SourceScope { get; set; }
}

public class EffectiveApprovedAppPageDto
{
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string? Search { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public int Limit { get; set; }
    public int ReturnedItems { get; set; }
    public bool HasMore { get; set; }
    public IReadOnlyList<EffectiveApprovedAppDto> Items { get; set; } = [];
}

public class ChocolateySyncResultDto
{
    public bool Success { get; set; }
    public int PackagesUpserted { get; set; }
    public int PagesProcessed { get; set; }
    public DateTime SyncedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
}

public class WingetSyncResultDto
{
    public bool Success { get; set; }
    public int PackagesUpserted { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime? SourceGeneratedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
}
