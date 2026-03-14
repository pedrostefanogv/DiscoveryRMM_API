using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAutomationTaskAuditRepository
{
    Task CreateAsync(AutomationTaskAudit audit);
    Task<IReadOnlyList<AutomationTaskAudit>> GetByTaskIdAsync(Guid taskId, int limit = 100);
}
