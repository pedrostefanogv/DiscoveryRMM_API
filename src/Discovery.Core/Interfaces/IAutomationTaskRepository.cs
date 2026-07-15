using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAutomationTaskRepository
{
    Task<AutomationTaskDefinition> CreateAsync(AutomationTaskDefinition task);
    Task<AutomationTaskDefinition?> GetByIdAsync(Guid id, bool includeInactive = false);
    Task<AutomationTaskDefinition?> GetByIdIncludingDeletedAsync(Guid id, bool includeInactive = false);

    Task<IReadOnlyList<AutomationTaskDefinition>> GetListPageAsync(
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
        string? cursor,
        int limit);
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

    /// <summary>
    /// Returns active tasks for an agent, resolving hierarchical scope (Global → Client → Site → Agent).
    /// </summary>
    Task<IReadOnlyList<AutomationTaskDefinition>> GetActiveTasksForAgentAsync(
        Guid agentId,
        Guid? agentSiteId,
        Guid? siteClientId,
        int limit = 200);
}
