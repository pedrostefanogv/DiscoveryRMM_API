using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AppStore.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AppStore;

// existentes
public sealed class SearchAppStoreQueryHandler : IRequestHandler<SearchAppStoreQuery, Result<IReadOnlyList<AppStoreDto>>>
{ public Task<Result<IReadOnlyList<AppStoreDto>>> Handle(SearchAppStoreQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<AppStoreDto>>.Success(Array.Empty<AppStoreDto>())); }

public sealed class GetAppStoreEffectiveAppsQueryHandler : IRequestHandler<GetAppStoreEffectiveAppsQuery, Result<IReadOnlyList<AppStoreDto>>>
{ public Task<Result<IReadOnlyList<AppStoreDto>>> Handle(GetAppStoreEffectiveAppsQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<AppStoreDto>>.Success(Array.Empty<AppStoreDto>())); }

public sealed class GetAppStoreCatalogQueryHandler : IRequestHandler<GetAppStoreCatalogQuery, Result<IReadOnlyList<AppStoreDto>>>
{ public Task<Result<IReadOnlyList<AppStoreDto>>> Handle(GetAppStoreCatalogQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<AppStoreDto>>.Success(Array.Empty<AppStoreDto>())); }

// novos — approvals
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

// novos — audit
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

// novos — package diff
public sealed class GetAppStorePackageDiffQueryHandler(IAppStoreService svc)
    : IRequestHandler<GetAppStorePackageDiffQuery, Result<AppApprovalPackageDiffDto>>
{
    public async Task<Result<AppApprovalPackageDiffDto>> Handle(GetAppStorePackageDiffQuery q, CancellationToken ct)
    {
        var diff = await svc.GetPackageDiffAsync(q.ScopeType, q.ScopeId, q.InstallationType, q.PackageId, ct);
        return Result<AppApprovalPackageDiffDto>.Success(diff);
    }
}

// novos — effective diffs
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

// novos — sync
public sealed class SyncAppStoreCatalogCommandHandler(IAppCatalogSyncService syncSvc)
    : IRequestHandler<SyncAppStoreCatalogCommand, Result<AppCatalogSyncResultDto>>
{
    public async Task<Result<AppCatalogSyncResultDto>> Handle(SyncAppStoreCatalogCommand cmd, CancellationToken ct)
    {
        var result = await syncSvc.SyncCatalogAsync(cmd.InstallationType, ct);
        return Result<AppCatalogSyncResultDto>.Success(result);
    }
}