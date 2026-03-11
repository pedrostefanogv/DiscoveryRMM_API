using Meduza.Core.Entities;
using Pgvector;

namespace Meduza.Core.Interfaces;

public interface IKnowledgeChunkRepository
{
    /// <summary>
    /// Busca semântica por cosine distance no escopo especificado.
    /// Inclui artigos do site + client + global.
    /// </summary>
    Task<List<KnowledgeChunkSearchResult>> SearchSemanticAsync(
        Vector queryEmbedding,
        Guid? clientId,
        Guid? siteId,
        int limit = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Chunks sem embedding gerado (para background service)
    /// </summary>
    Task<List<KnowledgeArticleChunk>> GetChunksWithoutEmbeddingAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Remove todos os chunks de um artigo e insere os novos (re-chunking)
    /// </summary>
    Task ReplaceAllForArticleAsync(Guid articleId, List<KnowledgeArticleChunk> newChunks, CancellationToken ct = default);

    Task UpdateEmbeddingAsync(Guid chunkId, Vector embedding, CancellationToken ct = default);
}

public record KnowledgeChunkSearchResult(
    Guid ArticleId,
    string ArticleTitle,
    Guid? ArticleClientId,
    Guid? ArticleSiteId,
    string? SectionTitle,
    string ChunkContent,
    double Distance);
