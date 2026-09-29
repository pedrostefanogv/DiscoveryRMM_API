using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Queue for triggering asynchronous label reprocessing across all agents.
/// Enqueuing returns immediately so the HTTP request does not block on a
/// potentially long-running batch operation.
/// </summary>
public interface ILabelReprocessQueue
{
    /// <summary>
    /// Enfileira o reprocessamento e devolve o id do job para acompanhamento.
    /// Se ja existir um job em andamento, devolve o id existente em vez de duplicar
    /// a passagem completa pela frota.
    /// </summary>
    /// <param name="actor">Autor do disparo, gravado na auditoria das mudancas de label.</param>
    /// <param name="coalesce">
    /// Quando true (default), um job ja ativo absorve o pedido e o id existente e devolvido.
    /// Use false quando o pedido precisa GARANTIR uma nova passagem (ex.: apos excluir uma
    /// regra, para remover as labels orfas mesmo que um reprocessamento esteja em andamento).
    /// </param>
    ValueTask<string> EnqueueAsync(string? actor = null, CancellationToken cancellationToken = default, bool coalesce = true);

    /// <summary>Consulta o progresso de um job. Retorna null quando o id e desconhecido.</summary>
    Task<AgentLabelReprocessStatusResponse?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default);
}
