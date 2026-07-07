using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IReportFilterPresetRepository
{
    Task<ReportFilterPreset> CreateAsync(ReportFilterPreset preset);
    Task<IReadOnlyList<ReportFilterPreset>> GetByUserAsync(Guid userId, Guid templateId);
    Task<ReportFilterPreset?> GetByIdAsync(Guid id, Guid userId);
    Task<ReportFilterPreset?> UpdateAsync(ReportFilterPreset preset);
    Task<bool> DeleteAsync(Guid id, Guid userId);
}
