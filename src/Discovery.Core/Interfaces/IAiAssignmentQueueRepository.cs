using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>Fila de triagem por IA (claim em lote, retry com backoff).</summary>
public interface IAiAssignmentQueueRepository
{
    Task EnqueueAsync(Guid ticketId, Guid departmentId, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Escopos (clientes) com itens pendentes na fila, do mais antigo para o mais
    /// novo. Departamento global entra como Guid.Empty.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListPendingClientScopesAsync(int maxScopes, CancellationToken ct = default);

    /// <summary>
    /// Reclama até limit itens pendentes DE UM cliente (cota por escopo, fairness
    /// entre clientes no mesmo ciclo).
    /// </summary>
    Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchForClientAsync(
        Guid clientId, int limit, CancellationToken ct = default);
    Task MarkDoneAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default);
    Task MarkSkippedAsync(Guid id, string reason, CancellationToken ct = default);
    Task<int> CountOutstandingAsync(CancellationToken ct = default);
}
