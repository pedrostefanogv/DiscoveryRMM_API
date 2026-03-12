using Meduza.Core.DTOs;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAppStoreService
{
    Task<AppCatalogSearchResultDto> SearchCatalogAsync(
        AppInstallationType installationType,
        string? search,
        string? architecture,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(
        AppInstallationType installationType,
        string packageId,
        CancellationToken cancellationToken = default);

    Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(
        UpsertCustomAppCatalogPackageRequest request,
        CancellationToken cancellationToken = default);

    Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string packageId,
        AppApprovalActionType action,
        bool? autoUpdateEnabled,
        string? reason,
        string? changedBy,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default);

    Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(
        AppInstallationType installationType,
        string? packageId,
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        string? changedBy,
        DateTime? changedFrom,
        DateTime? changedTo,
        AppApprovalAuditChangeType? changeType,
        int limit,
        Guid? cursor,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);

    Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string packageId,
        CancellationToken cancellationToken = default);

    Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);
}
