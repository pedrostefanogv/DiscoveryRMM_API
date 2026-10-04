using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IReportTemplateRepository
{
    Task<ReportTemplate> CreateAsync(ReportTemplate template);
    Task<ReportTemplate?> GetByIdAsync(Guid id, Guid? clientId = null);
    Task<IReadOnlyList<ReportTemplate>> GetAllAsync(Guid? clientId = null, ReportDatasetType? datasetType = null, bool? isActive = true);
    Task<IReadOnlyList<ReportTemplateHistory>> GetHistoryAsync(Guid templateId, int limit = 50);
    Task UpdateAsync(ReportTemplate template);
    Task<bool> DeleteAsync(Guid id, Guid? clientId = null);

    /// <summary>
    /// Remove snapshots de historico mais antigos que o corte. Sem retencao, a
    /// tabela report_template_history cresce indefinidamente.
    /// </summary>
    Task<int> DeleteHistoryOlderThanAsync(DateTime cutoff);

    /// <summary>Templates embutidos (biblioteca), opcionalmente por dataset.</summary>
    Task<IReadOnlyList<ReportTemplate>> GetBuiltInAsync(ReportDatasetType? datasetType = null);
}
