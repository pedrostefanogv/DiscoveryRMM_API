using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAppApprovalAuditRepository
{
    Task CreateAsync(AppApprovalAudit audit);
    Task<IReadOnlyList<AppApprovalAudit>> GetHistoryAsync(
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
