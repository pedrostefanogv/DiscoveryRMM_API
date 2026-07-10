using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.AppStore.Commands;

// ── Custom package management ────────────────────────────────────────
public sealed record UpsertCustomAppPackageCommand(
    string PackageId,
    string Name,
    string? Publisher = null,
    string? Version = null,
    string? Description = null,
    string? IconUrl = null,
    string? SiteUrl = null,
    string? InstallCommand = null,
    string? MetadataJson = null,
    string? FileObjectKey = null,
    string? FileBucket = null,
    string? FilePublicUrl = null,
    string? FileContentType = null,
    long? FileSizeBytes = null,
    string? FileChecksum = null
) : ICommand<Result<AppCatalogPackageDto>>;

// ── Approval rules ───────────────────────────────────────────────────
public sealed record UpsertAppApprovalRuleCommand(
    AppApprovalScopeType ScopeType,
    Guid? ScopeId,
    int InstallationType,
    string PackageId,
    int Action,
    bool? AutoUpdateEnabled,
    string? Reason,
    string? ChangedBy,
    string? IpAddress
) : ICommand<Result<AppApprovalRuleResolvedDto>>;

public sealed record DeleteAppApprovalRuleCommand(
    Guid RuleId,
    string? Reason = null,
    string? ChangedBy = null,
    string? IpAddress = null
) : ICommand<Result<VoidResult>>;

// ── Sync ─────────────────────────────────────────────────────────────
public sealed record SyncAppStoreCatalogCommand(
    AppInstallationType InstallationType = AppInstallationType.Winget
) : ICommand<Result<AppCatalogSyncResultDto>>;
