using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>Fila de triagem por IA (claim em lote, retry com backoff).</summary>
public interface IAiAssignmentQueueRepository
{
    Task EnqueueAsync(Guid ticketId, Guid departmentId, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default);
    Task MarkDoneAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default);
    Task MarkSkippedAsync(Guid id, string reason, CancellationToken ct = default);
    Task<int> CountOutstandingAsync(CancellationToken ct = default);
}
