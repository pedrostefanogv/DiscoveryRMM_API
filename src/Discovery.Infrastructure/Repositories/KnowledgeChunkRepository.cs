using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class KnowledgeChunkRepository(DiscoveryDbContext db) : IKnowledgeChunkRepository
{
    /// <summary>
    /// Busca semântica por cosine distance com herança de escopo.
    /// Usa operador pgvector <=> para distância cosine.
    /// A distância é calculada UMA única vez via projeção SELECT antes do ORDER BY.
    /// Artigos Internal só são retornados se departmentId corresponder.
    /// </summary>
    public async Task<List<KnowledgeChunkSearchResult>> SearchSemanticAsync(
        Vector queryEmbedding,
        Guid? clientId,
        Guid? siteId,
        int limit = 5,
        double minSimilarity = 0.0,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var chunksQuery = db.KnowledgeArticleChunks
            .Include(c => c.Article)
            .Where(c =>
                c.Embedding != null &&
                c.Article.DeletedAt == null &&
                (c.Article.Status == ArticleStatus.Published.ToString() ||
                 c.Article.Status == ArticleStatus.Internal.ToString()));

        // Filtro de herança de escopo via artigo pai
        chunksQuery = (clientId, siteId) switch
        {
            (not null, not null) => chunksQuery.Where(c =>
                (c.Article.SiteId == siteId) ||
                (c.Article.ClientId == clientId && c.Article.SiteId == null) ||
                (c.Article.ClientId == null && c.Article.SiteId == null)),

            (not null, null) => chunksQuery.Where(c =>
                (c.Article.ClientId == clientId && c.Article.SiteId == null) ||
                (c.Article.ClientId == null && c.Article.SiteId == null)),

            _ => chunksQuery.Where(c => c.Article.ClientId == null && c.Article.SiteId == null)
        };

        // Filtro de departamento para artigos Internal
        if (departmentId.HasValue)
        {
            chunksQuery = chunksQuery.Where(c =>
                c.Article.Status != ArticleStatus.Internal.ToString() ||
                c.Article.DepartmentId == departmentId.Value);
        }
        else
        {
            // Sem departmentId, não retorna artigos Internal
            chunksQuery = chunksQuery.Where(c =>
                c.Article.Status != ArticleStatus.Internal.ToString());
        }

        // Excluir artigos já injetados no system prompt (evita duplicação no RAG + tool call)
        if (excludeArticleIds != null && excludeArticleIds.Count > 0)
            chunksQuery = chunksQuery.Where(c => !excludeArticleIds.Contains(c.ArticleId));

        // Projetar a distância UMA VEZ (evita double CosineDistance no SQL):
        // EF Core envolve em subquery → ORDER BY referencia o alias calculado
        // Take extra (limit*2) para compensar o filtro de minSimilarity aplicado em memória
        var fetchLimit = minSimilarity > 0.0 ? Math.Min(limit * 2, limit + 10) : limit;

        var rows = await chunksQuery
            .Select(c => new
            {
                c.ArticleId,
                ArticleTitle = c.Article.Title,
                ArticleClientId = c.Article.ClientId,
                ArticleSiteId = c.Article.SiteId,
                c.SectionTitle,
                Content = c.Content,
                Distance = (double)c.Embedding!.CosineDistance(queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .Take(fetchLimit)
            .ToListAsync(ct);

        // Filtrar por similaridade mínima em memória (1 - distance = similarity)
        var filtered = minSimilarity > 0.0
            ? rows.Where(x => 1.0 - x.Distance >= minSimilarity).Take(limit).ToList()
            : rows;

        return filtered
            .Select(x => new KnowledgeChunkSearchResult(
                x.ArticleId,
                x.ArticleTitle,
                x.ArticleClientId,
                x.ArticleSiteId,
                x.SectionTitle,
                x.Content,
                x.Distance))
            .ToList();
    }

    public async Task<List<KnowledgeArticleChunk>> GetChunksWithoutEmbeddingAsync(
        int limit = 20,
        CancellationToken ct = default)
        => await db.KnowledgeArticleChunks
            .Include(c => c.Article)
            .Where(c => c.EmbeddingGeneratedAt == null
                && (c.Article.Status == ArticleStatus.Published.ToString() ||
                    c.Article.Status == ArticleStatus.Internal.ToString())
                && c.Article.DeletedAt == null)
            .OrderBy(c => c.ArticleId).ThenBy(c => c.ChunkIndex)
            .Take(limit)
            .ToListAsync(ct);

    public async Task ReplaceAllForArticleAsync(
        Guid articleId,
        List<KnowledgeArticleChunk> newChunks,
        CancellationToken ct = default)
    {
        var existing = await db.KnowledgeArticleChunks
            .Where(c => c.ArticleId == articleId)
            .ToListAsync(ct);

        db.KnowledgeArticleChunks.RemoveRange(existing);

        foreach (var chunk in newChunks)
        {
            chunk.Id = IdGenerator.NewId();
            chunk.ArticleId = articleId;
        }

        db.KnowledgeArticleChunks.AddRange(newChunks);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateEmbeddingAsync(Guid chunkId, Vector embedding, CancellationToken ct = default)
    {
        var chunk = await db.KnowledgeArticleChunks.FindAsync([chunkId], ct);
        if (chunk == null) return;
        chunk.Embedding = embedding;
        chunk.EmbeddingGeneratedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Busca semântica em múltiplos escopos (ACL do usuário).
    /// </summary>
    public async Task<List<KnowledgeChunkSearchResult>> SearchSemanticByUserScopeAsync(
        Vector queryEmbedding,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        int limit = 5,
        double minSimilarity = 0.0,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var chunksQuery = db.KnowledgeArticleChunks
            .Include(c => c.Article)
            .Where(c =>
                c.Embedding != null &&
                c.Article.DeletedAt == null &&
                (c.Article.Status == ArticleStatus.Published.ToString() ||
                 c.Article.Status == ArticleStatus.Internal.ToString()));

        // Filtro multi-escopo
        chunksQuery = ApplyMultiScopeFilter(chunksQuery, hasGlobalAccess, allowedClientIds, allowedSiteIds);

        // Filtro de departamento
        if (departmentId.HasValue)
        {
            chunksQuery = chunksQuery.Where(c =>
                c.Article.Status != ArticleStatus.Internal.ToString() ||
                c.Article.DepartmentId == departmentId.Value);
        }
        else
        {
            chunksQuery = chunksQuery.Where(c =>
                c.Article.Status != ArticleStatus.Internal.ToString());
        }

        if (excludeArticleIds != null && excludeArticleIds.Count > 0)
            chunksQuery = chunksQuery.Where(c => !excludeArticleIds.Contains(c.ArticleId));

        var fetchLimit = minSimilarity > 0.0 ? Math.Min(limit * 2, limit + 10) : limit;

        var rows = await chunksQuery
            .Select(c => new
            {
                c.ArticleId,
                ArticleTitle = c.Article.Title,
                ArticleClientId = c.Article.ClientId,
                ArticleSiteId = c.Article.SiteId,
                c.SectionTitle,
                Content = c.Content,
                Distance = (double)c.Embedding!.CosineDistance(queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .Take(fetchLimit)
            .ToListAsync(ct);

        var filtered = minSimilarity > 0.0
            ? rows.Where(x => 1.0 - x.Distance >= minSimilarity).Take(limit).ToList()
            : rows;

        return filtered
            .Select(x => new KnowledgeChunkSearchResult(
                x.ArticleId,
                x.ArticleTitle,
                x.ArticleClientId,
                x.ArticleSiteId,
                x.SectionTitle,
                x.Content,
                x.Distance))
            .ToList();
    }

    /// <summary>
    /// Aplica cláusula WHERE de escopo sobre a tabela de chunks (via Article).
    /// </summary>
    private static IQueryable<KnowledgeArticleChunk> ApplyMultiScopeFilter(
        IQueryable<KnowledgeArticleChunk> query,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds)
    {
        if (hasGlobalAccess)
            return query;

        var clientList = allowedClientIds.ToList();
        var siteList = allowedSiteIds.ToList();

        if (clientList.Count == 0 && siteList.Count == 0)
        {
            return query.Where(c =>
                c.Article.ClientId == null && c.Article.SiteId == null);
        }

        if (clientList.Count > 0 && siteList.Count > 0)
        {
            return query.Where(c =>
                (c.Article.ClientId == null && c.Article.SiteId == null) ||
                (c.Article.ClientId != null && c.Article.SiteId == null && clientList.Contains(c.Article.ClientId.Value)) ||
                (c.Article.SiteId != null && siteList.Contains(c.Article.SiteId.Value)));
        }

        if (clientList.Count > 0)
        {
            return query.Where(c =>
                (c.Article.ClientId == null && c.Article.SiteId == null) ||
                (c.Article.ClientId != null && c.Article.SiteId == null && clientList.Contains(c.Article.ClientId.Value)) ||
                (c.Article.SiteId != null && clientList.Contains(c.Article.ClientId!.Value)));
        }

        return query.Where(c =>
            (c.Article.ClientId == null && c.Article.SiteId == null) ||
            (c.Article.SiteId != null && siteList.Contains(c.Article.SiteId.Value)));
    }
}
