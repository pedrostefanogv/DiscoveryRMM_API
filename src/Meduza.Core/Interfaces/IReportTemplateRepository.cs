using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IReportTemplateRepository
{
    Task<ReportTemplate> CreateAsync(ReportTemplate template);
    Task<ReportTemplate?> GetByIdAsync(Guid id, Guid? clientId = null);
    Task<IReadOnlyList<ReportTemplate>> GetAllAsync(Guid? clientId = null, ReportDatasetType? datasetType = null, bool? isActive = true);
    Task<IReadOnlyList<ReportTemplateHistory>> GetHistoryAsync(Guid templateId, int limit = 50);
    Task UpdateAsync(ReportTemplate template);
    Task<bool> DeleteAsync(Guid id, Guid? clientId = null);
}
