using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

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
