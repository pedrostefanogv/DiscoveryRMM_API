using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IKnowledgeEmbeddingQueueRepository
{
    Task EnqueueAsync(Guid articleId, string? reason, CancellationToken ct = default);
    Task<List<KnowledgeEmbeddingQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default);
    Task MarkDoneAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default);
}
