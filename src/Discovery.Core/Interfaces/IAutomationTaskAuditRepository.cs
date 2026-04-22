using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAutomationTaskAuditRepository
{
    Task CreateAsync(AutomationTaskAudit audit);
    Task<IReadOnlyList<AutomationTaskAudit>> GetByTaskIdAsync(Guid taskId, int limit = 100);
}
