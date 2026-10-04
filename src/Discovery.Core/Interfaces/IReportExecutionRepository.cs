using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IReportExecutionRepository
{
    Task<ReportExecution> CreateAsync(ReportExecution execution);
    Task<ReportExecution?> GetByIdAsync(Guid id, Guid? clientId = null);
    Task<IReadOnlyList<ReportExecution>> GetRecentByClientAsync(Guid? clientId = null, int limit = 50);
    Task<IReadOnlyList<ReportExecution>> GetRecentByClientIdsAsync(IReadOnlyCollection<Guid> clientIds, int limit = 50);
    Task<IReadOnlyList<ReportExecution>> GetPendingAsync(int limit = 20);
    Task<IReadOnlyList<ReportExecution>> GetExpiredAsync(DateTime cutoff, int limit = 1000);
    Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids);
    Task UpdateStatusAsync(Guid id, Guid? clientId, ReportExecutionStatus status, string? errorMessage = null);

    /// <summary>
    /// Claim atomico: marca a execucao como Running SOMENTE se ela ainda estiver
    /// Pending. Em multiplas replicas, apenas quem afetar 1 linha deve processar
    /// (evita upload/execucao duplicados).
    /// </summary>
    Task<bool> TryClaimPendingAsync(Guid id, Guid? clientId = null);

    /// <summary>
    /// Reenfileira execucoes que ficaram presas em Running (processo morto no
    /// meio do processamento). Sem isso elas nunca voltam para a fila.
    /// </summary>
    Task<int> RequeueStaleRunningAsync(DateTime startedBefore, int limit = 100);
    Task UpdateResultAsync(
        Guid id,
        Guid? clientId,
        string storageObjectKey,
        string storageBucket,
        string storageContentType,
        long storageSizeBytes,
        string? storageChecksum,
        int storageProviderType,
        int rowCount,
        int executionTimeMs);
}
