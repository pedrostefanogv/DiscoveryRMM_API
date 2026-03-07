using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IReportService
{
    Task<ReportExecution> ProcessExecutionAsync(Guid executionId, Guid clientId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportExecution>> ProcessPendingAsync(int maxItems, CancellationToken cancellationToken = default);
    Task<(byte[] Content, string ContentType, string FileName)?> GetDownloadAsync(Guid executionId, Guid clientId, CancellationToken cancellationToken = default);
}
