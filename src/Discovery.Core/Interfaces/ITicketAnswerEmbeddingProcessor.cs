using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Processa o backfill de embeddings das respostas do questionário. Executado
/// dentro do ciclo do job de embeddings de IA (dono único do auto-sync de dimensão).
/// </summary>
public interface ITicketAnswerEmbeddingProcessor
{
    /// <summary>Processa até o teto por ciclo e devolve quantas respostas foram embedadas.</summary>
    Task<int> ProcessAsync(AIIntegrationSettings settings, CancellationToken ct = default);
}
