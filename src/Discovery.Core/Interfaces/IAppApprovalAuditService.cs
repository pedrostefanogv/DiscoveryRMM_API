using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAppApprovalAuditService
{
    Task LogAsync(
        AppApprovalAuditChangeType changeType,
        AppApprovalRule? currentRule,
        AppApprovalActionType? oldAction,
        bool? oldAutoUpdateEnabled,
        string? reason,
        string? changedBy,
        string? ipAddress);

    Task<AppApprovalAuditPageDto> GetHistoryAsync(
        AppInstallationType installationType,
        string? packageId,
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        string? changedBy,
        DateTime? changedFrom,
        DateTime? changedTo,
        AppApprovalAuditChangeType? changeType,
        int limit = 100,
        Guid? cursor = null);
}
