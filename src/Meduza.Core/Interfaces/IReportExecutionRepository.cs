using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IReportExecutionRepository
{
    Task<ReportExecution> CreateAsync(ReportExecution execution);
    Task<ReportExecution?> GetByIdAsync(Guid id, Guid? clientId = null);
    Task<IReadOnlyList<ReportExecution>> GetRecentByClientAsync(Guid? clientId = null, int limit = 50);
    Task<IReadOnlyList<ReportExecution>> GetPendingAsync(int limit = 20);
    Task UpdateStatusAsync(Guid id, Guid? clientId, ReportExecutionStatus status, string? errorMessage = null);
    Task UpdateResultAsync(Guid id, Guid? clientId, string resultPath, string resultContentType, long resultSizeBytes, int rowCount, int executionTimeMs);
}
