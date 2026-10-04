using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IReportService
{
    Task<ReportExecution> ProcessExecutionAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default, ReportQueryScope? scope = null);
    Task<IReadOnlyList<ReportExecution>> ProcessPendingAsync(int maxItems, CancellationToken cancellationToken = default);
    Task<string?> GetPresignedDownloadUrlAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Baixa o arquivo gerado pela execucao, servindo o conteudo pela API.
    /// Funciona com qualquer provedor de object storage (inclusive o local,
    /// cuja URL pre-assinada nao e navegavel).
    /// </summary>
    Task<ReportDownloadResult?> GetDownloadAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default);
    Task<ReportPreviewResult> PreviewAsync(ReportTemplate template, ReportFormat format, string? filtersJson = null, CancellationToken cancellationToken = default, ReportQueryScope? scope = null);
    Task<ReportHtmlPreviewResult> PreviewHtmlAsync(ReportTemplate template, string? filtersJson = null, CancellationToken cancellationToken = default, ReportQueryScope? scope = null);
}
