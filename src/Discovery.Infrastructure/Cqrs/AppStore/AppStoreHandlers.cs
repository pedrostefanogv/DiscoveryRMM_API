using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AppStore.Commands;
using Discovery.Core.Cqrs.AppStore.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AppStore;

// ── Catalog ──────────────────────────────────────────────────────────

public sealed class SearchAppStoreQueryHandler(IAppStoreService svc)
    : IRequestHandler<SearchAppStoreQuery, Result<AppCatalogSearchResultDto>>
{
    public async Task<Result<AppCatalogSearchResultDto>> Handle(SearchAppStoreQuery q, CancellationToken ct)
    {
        var result = await svc.SearchCatalogAsync(q.InstallationType, q.Search, q.Architecture, q.Limit, q.Cursor, ct);
        return Result<AppCatalogSearchResultDto>.Success(result);
    }
}

public sealed class GetAppStoreCatalogQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreCatalogQuery, Result<AppCatalogSearchResultDto>>
{
    public async Task<Result<AppCatalogSearchResultDto>> Handle(GetAppStoreCatalogQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)q.InstallationType;
        var result = await svc.SearchCatalogAsync(instType, q.Search, q.Architecture, q.Limit, q.Cursor, ct);
        return Result<AppCatalogSearchResultDto>.Success(result);
    }
}

public sealed class GetCatalogPackageByIdQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetCatalogPackageByIdQuery, Result<AppCatalogPackageDto>>
{
    public async Task<Result<AppCatalogPackageDto>> Handle(GetCatalogPackageByIdQuery q, CancellationToken ct)
    {
        var pkg = await svc.GetCatalogPackageByIdAsync((AppInstallationType)q.InstallationType, q.PackageId, ct);
        return pkg is null
            ? Result<AppCatalogPackageDto>.Failure(Error.NotFound($"Package '{q.PackageId}' not found."))
            : Result<AppCatalogPackageDto>.Success(pkg);
    }
}

// ── Custom package ───────────────────────────────────────────────────

public sealed class UpsertCustomAppPackageCommandHandler(IAppStoreService svc)
    : IRequestHandler<UpsertCustomAppPackageCommand, Result<AppCatalogPackageDto>>
{
    public async Task<Result<AppCatalogPackageDto>> Handle(UpsertCustomAppPackageCommand cmd, CancellationToken ct)
    {
        var request = new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = cmd.PackageId,
            Name = cmd.Name,
            Publisher = cmd.Publisher,
            Version = cmd.Version,
            Description = cmd.Description,
            IconUrl = cmd.IconUrl,
            SiteUrl = cmd.SiteUrl,
            InstallCommand = cmd.InstallCommand,
            MetadataJson = cmd.MetadataJson,
            FileObjectKey = cmd.FileObjectKey,
            FileBucket = cmd.FileBucket,
            FilePublicUrl = cmd.FilePublicUrl,
            FileContentType = cmd.FileContentType,
            FileSizeBytes = cmd.FileSizeBytes,
            FileChecksum = cmd.FileChecksum
        };
        var result = await svc.UpsertCustomCatalogPackageAsync(request, ct);
        return Result<AppCatalogPackageDto>.Success(result);
    }
}

// ── Effective apps ───────────────────────────────────────────────────

public sealed class GetAppStoreEffectiveAppsQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreEffectiveAppsQuery, Result<IReadOnlyList<EffectiveApprovedAppDto>>>
{
    public async Task<Result<IReadOnlyList<EffectiveApprovedAppDto>>> Handle(GetAppStoreEffectiveAppsQuery q, CancellationToken ct)
    {
        var apps = await svc.GetEffectiveAppsAsync(q.ClientId, q.SiteId, q.AgentId, q.InstallationType, ct);
        return Result<IReadOnlyList<EffectiveApprovedAppDto>>.Success(apps);
    }
}

// ── Approvals ────────────────────────────────────────────────────────

public sealed class GetAppStoreApprovalsQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreApprovalsQuery, Result<IReadOnlyList<AppApprovalRuleResolvedDto>>>
{
    public async Task<Result<IReadOnlyList<AppApprovalRuleResolvedDto>>> Handle(GetAppStoreApprovalsQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)(q.InstallationType ?? 0);
        var rules = await svc.GetRulesByScopeAsync(q.ScopeType ?? AppApprovalScopeType.Global, q.ScopeId, instType, ct);
        return Result<IReadOnlyList<AppApprovalRuleResolvedDto>>.Success(rules);
    }
}

public sealed class UpsertAppApprovalRuleCommandHandler(IAppStoreService svc)
    : IRequestHandler<UpsertAppApprovalRuleCommand, Result<AppApprovalRuleResolvedDto>>
{
    public async Task<Result<AppApprovalRuleResolvedDto>> Handle(UpsertAppApprovalRuleCommand cmd, CancellationToken ct)
    {
        var rule = await svc.UpsertRuleAsync(
            cmd.ScopeType, cmd.ScopeId,
            (AppInstallationType)cmd.InstallationType, cmd.PackageId,
            (AppApprovalActionType)cmd.Action, cmd.AutoUpdateEnabled,
            cmd.Reason, cmd.ChangedBy, cmd.IpAddress, ct);
        return Result<AppApprovalRuleResolvedDto>.Success(rule);
    }
}

public sealed class DeleteAppApprovalRuleCommandHandler(IAppStoreService svc)
    : IRequestHandler<DeleteAppApprovalRuleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAppApprovalRuleCommand cmd, CancellationToken ct)
    {
        await svc.DeleteRuleAsync(cmd.RuleId, cmd.Reason, cmd.ChangedBy, cmd.IpAddress, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

// ── Audit ────────────────────────────────────────────────────────────

public sealed class GetAppStoreApprovalAuditQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreApprovalAuditQuery, Result<AppApprovalAuditPageDto>>
{
    public async Task<Result<AppApprovalAuditPageDto>> Handle(GetAppStoreApprovalAuditQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)(q.InstallationType ?? 0);
        var page = await svc.GetAuditHistoryAsync(instType, q.PackageId, q.ScopeType, q.ScopeId, q.ChangedBy, q.ChangedFrom, q.ChangedTo, q.ChangeType, q.Limit, q.Cursor, ct);
        return Result<AppApprovalAuditPageDto>.Success(page);
    }
}

// ── Package diff ─────────────────────────────────────────────────────

public sealed class GetAppStorePackageDiffQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStorePackageDiffQuery, Result<AppApprovalPackageDiffDto>>
{
    public async Task<Result<AppApprovalPackageDiffDto>> Handle(GetAppStorePackageDiffQuery q, CancellationToken ct)
    {
        var diff = await svc.GetPackageDiffAsync(q.ScopeType, q.ScopeId, q.InstallationType, q.PackageId, ct);
        return Result<AppApprovalPackageDiffDto>.Success(diff);
    }
}

// ── Effective diffs ──────────────────────────────────────────────────

public sealed class GetAppStoreEffectiveDiffsQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStoreEffectiveDiffsQuery, Result<AppEffectivePackageDiffPageDto>>
{
    public async Task<Result<AppEffectivePackageDiffPageDto>> Handle(GetAppStoreEffectiveDiffsQuery q, CancellationToken ct)
    {
        var instType = (AppInstallationType)q.InstallationType;
        var page = await svc.GetEffectiveAppDiffsAsync(q.ScopeType, q.ScopeId, instType, q.Search, q.Limit, q.Cursor, ct);
        return Result<AppEffectivePackageDiffPageDto>.Success(page);
    }
}

// ── Sync ─────────────────────────────────────────────────────────────

public sealed class SyncAppStoreCatalogCommandHandler(IAppCatalogSyncService syncSvc)
    : IRequestHandler<SyncAppStoreCatalogCommand, Result<AppCatalogSyncResultDto>>
{
    public async Task<Result<AppCatalogSyncResultDto>> Handle(SyncAppStoreCatalogCommand cmd, CancellationToken ct)
    {
        var result = await syncSvc.SyncCatalogAsync(cmd.InstallationType, ct);
        return Result<AppCatalogSyncResultDto>.Success(result);
    }
}