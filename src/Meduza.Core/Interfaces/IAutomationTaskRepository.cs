using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAutomationTaskRepository
{
    Task<AutomationTaskDefinition> CreateAsync(AutomationTaskDefinition task);
    Task<AutomationTaskDefinition?> GetByIdAsync(Guid id, bool includeInactive = false);
    Task<AutomationTaskDefinition?> GetByIdIncludingDeletedAsync(Guid id, bool includeInactive = false);
    Task<IReadOnlyList<AutomationTaskDefinition>> GetListAsync(
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        bool activeOnly,
        bool deletedOnly,
        bool includeDeleted,
        string? search,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        IReadOnlyList<AppApprovalScopeType>? scopeTypes,
        IReadOnlyList<AutomationTaskActionType>? actionTypes,
        int limit,
        int offset);
    Task<int> CountAsync(
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        bool activeOnly,
        bool deletedOnly,
        bool includeDeleted,
        string? search,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        IReadOnlyList<AppApprovalScopeType>? scopeTypes,
        IReadOnlyList<AutomationTaskActionType>? actionTypes);
    Task UpdateAsync(AutomationTaskDefinition task);
    Task DeleteAsync(Guid id);
    Task<AutomationTaskDefinition?> RestoreAsync(Guid id);
}
