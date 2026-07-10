using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.AppStore.Queries;

// existing
public sealed record SearchAppStoreQuery(string? Search, string? Architecture, string? Cursor, int Limit = 20) : IQuery<Result<IReadOnlyList<AppStoreDto>>>;
public sealed record GetAppStoreCatalogQuery(int InstallationType, string? Search, string? Architecture, string? Cursor, int Limit = 20) : IQuery<Result<IReadOnlyList<AppStoreDto>>>;
public sealed record GetAppStoreEffectiveAppsQuery(Guid? ClientId, Guid? SiteId, Guid? AgentId) : IQuery<Result<IReadOnlyList<AppStoreDto>>>;
public sealed record AppStoreDto(string PackageId, string Name, string Version, string Publisher, string AppType);

// new — approvals
public sealed record GetAppStoreApprovalsQuery(
    AppApprovalScopeType? ScopeType = null,
    Guid? ScopeId = null,
    int? InstallationType = null) : IQuery<Result<IReadOnlyList<AppApprovalRuleResolvedDto>>>;

// new — audit history
public sealed record GetAppStoreApprovalAuditQuery(
    int? InstallationType = null,
    string? PackageId = null,
    AppApprovalScopeType? ScopeType = null,
    Guid? ScopeId = null,
    string? ChangedBy = null,
    DateTime? ChangedFrom = null,
    DateTime? ChangedTo = null,
    AppApprovalAuditChangeType? ChangeType = null,
    int Limit = 50,
    Guid? Cursor = null) : IQuery<Result<AppApprovalAuditPageDto>>;

// new — package diff
public sealed record GetAppStorePackageDiffQuery(
    AppInstallationType InstallationType,
    string PackageId,
    AppApprovalScopeType ScopeType,
    Guid? ScopeId) : IQuery<Result<AppApprovalPackageDiffDto>>;

// new — effective diffs
public sealed record GetAppStoreEffectiveDiffsQuery(
    AppApprovalScopeType ScopeType,
    Guid? ScopeId,
    int InstallationType,
    string? Search = null,
    int Limit = 50,
    string? Cursor = null) : IQuery<Result<AppEffectivePackageDiffPageDto>>;

// new — sync command
public sealed record SyncAppStoreCatalogCommand(
    AppInstallationType InstallationType = AppInstallationType.Winget) : ICommand<Result<AppCatalogSyncResultDto>>;