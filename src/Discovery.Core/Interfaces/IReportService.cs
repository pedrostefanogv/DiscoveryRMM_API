using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IReportService
{
    Task<ReportExecution> ProcessExecutionAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportExecution>> ProcessPendingAsync(int maxItems, CancellationToken cancellationToken = default);
    Task<string?> GetPresignedDownloadUrlAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default);
    Task<ReportPreviewResult> PreviewAsync(ReportTemplate template, ReportFormat format, string? filtersJson = null, CancellationToken cancellationToken = default);
    Task<ReportHtmlPreviewResult> PreviewHtmlAsync(ReportTemplate template, string? filtersJson = null, CancellationToken cancellationToken = default);
}
