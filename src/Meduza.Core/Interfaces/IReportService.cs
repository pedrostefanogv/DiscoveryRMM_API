using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IReportService
{
    Task<ReportExecution> ProcessExecutionAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportExecution>> ProcessPendingAsync(int maxItems, CancellationToken cancellationToken = default);
    Task<string?> GetPresignedDownloadUrlAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default);
}
