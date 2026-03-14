using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAutomationTaskRepository
{
    Task<AutomationTaskDefinition> CreateAsync(AutomationTaskDefinition task);
    Task<AutomationTaskDefinition?> GetByIdAsync(Guid id, bool includeInactive = false);
    Task<IReadOnlyList<AutomationTaskDefinition>> GetListAsync(AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly, int limit, int offset);
    Task<int> CountAsync(AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly);
    Task UpdateAsync(AutomationTaskDefinition task);
    Task DeleteAsync(Guid id);
}
