using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.AppStore.Queries;

// ── Catalog ──────────────────────────────────────────────────────────
public sealed record SearchAppStoreQuery(
    AppInstallationType InstallationType = AppInstallationType.Winget,
    string? Search = null,
    string? Architecture = null,
    string? Cursor = null,
    int Limit = 20
) : IQuery<Result<AppCatalogSearchResultDto>>;

public sealed record GetAppStoreCatalogQuery(
    int InstallationType,
    string? Search = null,
    string? Architecture = null,
    string? Cursor = null,
    int Limit = 20
) : IQuery<Result<AppCatalogSearchResultDto>>;

public sealed record GetCatalogPackageByIdQuery(
    int InstallationType,
    string PackageId
) : IQuery<Result<AppCatalogPackageDto>>;

// ── Effective apps ───────────────────────────────────────────────────
public sealed record GetAppStoreEffectiveAppsQuery(
    Guid? ClientId,
    Guid? SiteId,
    Guid? AgentId,
    AppInstallationType InstallationType = AppInstallationType.Winget
) : IQuery<Result<IReadOnlyList<EffectiveApprovedAppDto>>>;

// ── Approvals ────────────────────────────────────────────────────────
public sealed record GetAppStoreApprovalsQuery(
    AppApprovalScopeType? ScopeType = null,
    Guid? ScopeId = null,
    int? InstallationType = null
) : IQuery<Result<IReadOnlyList<AppApprovalRuleResolvedDto>>>;

// ── Audit ────────────────────────────────────────────────────────────
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
    Guid? Cursor = null
) : IQuery<Result<AppApprovalAuditPageDto>>;

// ── Package diff ─────────────────────────────────────────────────────
public sealed record GetAppStorePackageDiffQuery(
    AppInstallationType InstallationType,
    string PackageId,
    AppApprovalScopeType ScopeType,
    Guid? ScopeId
) : IQuery<Result<AppApprovalPackageDiffDto>>;

// ── Effective diffs ──────────────────────────────────────────────────
public sealed record GetAppStoreEffectiveDiffsQuery(
    AppApprovalScopeType ScopeType,
    Guid? ScopeId,
    int InstallationType,
    string? Search = null,
    int Limit = 50,
    string? Cursor = null
) : IQuery<Result<AppEffectivePackageDiffPageDto>>;

// kept for backward compat
public sealed record AppStoreDto(string PackageId, string Name, string Version, string Publisher, string AppType);