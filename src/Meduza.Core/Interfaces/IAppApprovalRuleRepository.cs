using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAppApprovalRuleRepository
{
    Task<AppApprovalRule?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<AppApprovalRule>> GetByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType);
    Task<IReadOnlyList<AppApprovalRule>> GetForInheritanceAsync(Guid clientId, Guid siteId, Guid agentId, AppInstallationType installationType);
    Task<AppApprovalRule?> GetByUniqueKeyAsync(
        AppApprovalScopeType scopeType,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType,
        string packageId);
    Task<AppApprovalRule> CreateAsync(AppApprovalRule rule);
    Task UpdateAsync(AppApprovalRule rule);
    Task DeleteAsync(Guid id);
}
