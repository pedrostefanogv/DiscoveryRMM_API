using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repository for report schedule CRUD and active schedule enumeration.
/// </summary>
public interface IReportScheduleRepository
{
    Task<ReportSchedule> CreateAsync(ReportSchedule schedule);
    Task<ReportSchedule?> GetByIdAsync(Guid id, Guid? clientId = null);
    Task<IReadOnlyList<ReportSchedule>> GetByTemplateAsync(Guid templateId, Guid? clientId = null);
    Task<IReadOnlyList<ReportSchedule>> GetAllAsync(Guid? clientId = null, bool? isActive = true);
    Task<IReadOnlyList<ReportSchedule>> GetDueSchedulesAsync(DateTime utcNow, int limit = 50);
    Task UpdateAsync(ReportSchedule schedule);
    Task<bool> DeleteAsync(Guid id, Guid? clientId = null);
}
