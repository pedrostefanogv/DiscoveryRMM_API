namespace Discovery.Core.Interfaces;

/// <summary>Estado do backfill de snapshots de métricas de um escopo.</summary>
public sealed record BackgroundBackfillStateDto(
    /// <summary>Guid.Empty = backfill global (todos os escopos).</summary>
    Guid ClientId,
    string Status,
    DateTime RequestedAt,
    string? RequestedBy,
    int Total,
    int Processed,
    bool PurgeOrphans,
    string? LastError,
    DateTime? CompletedAt);

/// <summary>
/// Backfill (recálculo forçado) dos snapshots de métricas: pedido idempotente,
/// execução em lotes pelo job, progresso persistido e cancelamento.
/// </summary>
public interface IBackgroundProcessingBackfillService
{
    /// <summary>
    /// Pede o backfill do escopo (null/Guid.Empty = global). Se já houver um pedido
    /// pendente/em andamento para o escopo, devolve o estado atual sem duplicar.
    /// </summary>
    Task<BackgroundBackfillStateDto> RequestAsync(
        Guid? clientId, bool purgeOrphans, string? requestedBy, CancellationToken ct = default);

    /// <summary>Cancela um pedido pendente/em andamento. Retorna false se não havia.</summary>
    Task<bool> CancelAsync(Guid? clientId, string? requestedBy, CancellationToken ct = default);

    /// <summary>Estado atual do escopo (null quando nunca foi pedido).</summary>
    Task<BackgroundBackfillStateDto?> GetStateAsync(Guid? clientId, CancellationToken ct = default);

    /// <summary>
    /// Processa um lote do pedido mais antigo pendente/em andamento e devolve o
    /// estado atualizado (null quando não há trabalho).
    /// </summary>
    Task<BackgroundBackfillStateDto?> ProcessNextBatchAsync(CancellationToken ct = default);
}
