using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAutomationScriptAuditRepository
{
    Task CreateAsync(AutomationScriptAudit audit);
    Task<IReadOnlyList<AutomationScriptAudit>> GetByScriptIdAsync(Guid scriptId, int limit = 100);
}
