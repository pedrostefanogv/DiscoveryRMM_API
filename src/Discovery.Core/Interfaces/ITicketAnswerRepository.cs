using Pgvector;

namespace Discovery.Core.Interfaces;

/// <summary>Resposta pendente de embedding (com o escopo do chamado para credenciais).</summary>
public sealed record TicketAnswerEmbeddingJobItem(
    Guid AnswerId,
    Guid TicketId,
    string QuestionKey,
    string QuestionLabel,
    string ValueText,
    Guid? ClientId,
    Guid? SiteId);

public sealed record TicketAnswerEmbeddingUpdate(Guid AnswerId, float[] Embedding);

/// <summary>Hit da busca semântica por resposta do questionário.</summary>
public sealed record TicketAnswerSearchHit(
    Guid TicketId,
    string TicketTitle,
    Guid? ClientId,
    Guid? SiteId,
    string QuestionKey,
    string QuestionLabel,
    string ValueText,
    double Distance,
    DateTime TicketCreatedAt);

/// <summary>
/// Acesso a dados das respostas do questionário para a busca semântica:
/// backfill de embeddings e consulta por cosine distance (pgvector).
/// </summary>
public interface ITicketAnswerRepository
{
    /// <summary>
    /// Próximas respostas pendentes de embedding (exclui sensíveis e vazias).
    /// Pendente = `embedding IS NULL AND embedding_generated_at IS NULL`.
    /// </summary>
    Task<IReadOnlyList<TicketAnswerEmbeddingJobItem>> GetWithoutEmbeddingAsync(
        int limit,
        CancellationToken ct = default);

    /// <summary>Grava os embeddings e marca `embedding_generated_at`.</summary>
    Task UpdateEmbeddingsAsync(
        IReadOnlyList<TicketAnswerEmbeddingUpdate> updates,
        CancellationToken ct = default);

    /// <summary>
    /// Marca respostas como processadas SEM embedding (curtas/booleanas), para
    /// não voltarem à fila a cada ciclo.
    /// </summary>
    Task MarkProcessedWithoutEmbeddingAsync(
        IReadOnlyList<Guid> answerIds,
        CancellationToken ct = default);

    /// <summary>
    /// Busca por similaridade (cosine). O recorte de ACL é aplicado no SQL.
    /// </summary>
    /// <summary>
    /// Fallback por texto (ILIKE) quando não há embeddings/provedor: busca no
    /// rótulo da pergunta e no valor, com o mesmo recorte de ACL.
    /// </summary>
    Task<IReadOnlyList<TicketAnswerSearchHit>> SearchKeywordAsync(
        string term,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        IReadOnlyCollection<Guid> allowedSiteIds,
        Guid? templateId,
        string? questionKey,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<TicketAnswerSearchHit>> SearchSemanticAsync(
        Vector queryEmbedding,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        IReadOnlyCollection<Guid> allowedSiteIds,
        Guid? templateId,
        string? questionKey,
        int limit,
        double minSimilarity,
        CancellationToken ct = default);
}
