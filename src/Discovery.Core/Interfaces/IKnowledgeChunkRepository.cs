using Discovery.Core.Entities;
using Pgvector;

namespace Discovery.Core.Interfaces;

public interface IKnowledgeChunkRepository
{
    /// <summary>
    /// Busca semântica por cosine distance no escopo especificado.
    /// Inclui artigos do site + client + global.
    /// Filtra artigos Internal pelo departamento do usuário.
    /// </summary>
    Task<List<KnowledgeChunkSearchResult>> SearchSemanticAsync(
        Vector queryEmbedding,
        Guid? clientId,
        Guid? siteId,
        int limit = 5,
        double minSimilarity = 0.0,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        Guid? departmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Busca semântica em múltiplos escopos (ACL do usuário).
    /// </summary>
    Task<List<KnowledgeChunkSearchResult>> SearchSemanticByUserScopeAsync(
        Vector queryEmbedding,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        int limit = 5,
        double minSimilarity = 0.0,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        Guid? departmentId = null,
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
