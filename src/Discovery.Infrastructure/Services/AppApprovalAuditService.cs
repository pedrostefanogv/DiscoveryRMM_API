using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public class AppApprovalAuditService : IAppApprovalAuditService
{
    private readonly IAppApprovalAuditRepository _repo;

    public AppApprovalAuditService(IAppApprovalAuditRepository repo)
    {
        _repo = repo;
    }

    public async Task LogAsync(
        AppApprovalAuditChangeType changeType,
        AppApprovalRule? currentRule,
        AppApprovalActionType? oldAction,
        bool? oldAutoUpdateEnabled,
        string? reason,
        string? changedBy,
        string? ipAddress)
    {
        if (currentRule is null)
            return;

        await _repo.CreateAsync(new AppApprovalAudit
        {
            RuleId = currentRule.Id,
            ChangeType = changeType,
            ScopeType = currentRule.ScopeType,
            ClientId = currentRule.ClientId,
            SiteId = currentRule.SiteId,
            AgentId = currentRule.AgentId,
            InstallationType = currentRule.InstallationType,
            PackageId = currentRule.PackageId,
            OldAction = oldAction,
            NewAction = changeType == AppApprovalAuditChangeType.Deleted ? null : currentRule.Action,
            OldAutoUpdateEnabled = oldAutoUpdateEnabled,
            NewAutoUpdateEnabled = changeType == AppApprovalAuditChangeType.Deleted ? null : currentRule.AutoUpdateEnabled,
            Reason = reason,
            ChangedBy = changedBy,
            IpAddress = ipAddress
        });
    }

    public async Task<AppApprovalAuditPageDto> GetHistoryAsync(
        AppInstallationType installationType,
        string? packageId,
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        string? changedBy,
        DateTime? changedFrom,
        DateTime? changedTo,
        AppApprovalAuditChangeType? changeType,
        int limit = 100,
        Guid? cursor = null)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var history = await _repo.GetHistoryAsync(
            installationType,
            packageId,
            scopeType,
            scopeId,
            changedBy,
            changedFrom,
            changedTo,
            changeType,
            safeLimit,
            cursor);

        var hasMore = history.Count > safeLimit - 1;
        var pageItems = history.Take(safeLimit).ToList();
        var items = pageItems.Select(x => new AppApprovalAuditEntryDto
        {
            Id = x.Id,
            RuleId = x.RuleId,
            ChangeType = x.ChangeType,
            ScopeType = x.ScopeType,
            ScopeId = x.ScopeType switch
            {
                AppApprovalScopeType.Client => x.ClientId,
                AppApprovalScopeType.Site => x.SiteId,
                AppApprovalScopeType.Agent => x.AgentId,
                _ => null
            },
            InstallationType = x.InstallationType,
            PackageId = x.PackageId,
            OldAction = x.OldAction,
            NewAction = x.NewAction,
            OldAutoUpdateEnabled = x.OldAutoUpdateEnabled,
            NewAutoUpdateEnabled = x.NewAutoUpdateEnabled,
            Reason = x.Reason,
            ChangedBy = x.ChangedBy,
            IpAddress = x.IpAddress,
            ChangedAt = x.ChangedAt
        }).ToList();

        return new AppApprovalAuditPageDto
        {
            InstallationType = installationType,
            PackageId = packageId,
            ScopeType = scopeType,
            ScopeId = scopeId,
            ChangedBy = changedBy,
            ChangedFrom = changedFrom,
            ChangedTo = changedTo,
            ChangeType = changeType,
            ReturnedItems = items.Count,
            Cursor = cursor,
            NextCursor = hasMore ? pageItems[^1].Id : null,
            Limit = safeLimit,
            HasMore = hasMore,
            Items = items
        };
    }
}
