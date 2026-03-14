using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAutomationScriptRepository
{
    Task<AutomationScriptDefinition> CreateAsync(AutomationScriptDefinition script);
    Task<AutomationScriptDefinition?> GetByIdAsync(Guid id, bool includeInactive = false);
    Task<IReadOnlyList<AutomationScriptDefinition>> GetListAsync(Guid? clientId, bool activeOnly, int limit, int offset);
    Task<int> CountAsync(Guid? clientId, bool activeOnly);
    Task UpdateAsync(AutomationScriptDefinition script);
    Task DeleteAsync(Guid id);
}
