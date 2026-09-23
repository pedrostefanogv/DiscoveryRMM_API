using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Queue for triggering asynchronous label reprocessing across all agents.
/// Enqueuing returns immediately so the HTTP request does not block on a
/// potentially long-running batch operation.
/// </summary>
public interface ILabelReprocessQueue
{
    /// <summary>Enfileira o reprocessamento e devolve o id do job para acompanhamento.</summary>
    ValueTask<string> EnqueueAsync(CancellationToken cancellationToken = default);

    /// <summary>Consulta o progresso de um job. Retorna null quando o id e desconhecido.</summary>
    Task<AgentLabelReprocessStatusResponse?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default);
}
