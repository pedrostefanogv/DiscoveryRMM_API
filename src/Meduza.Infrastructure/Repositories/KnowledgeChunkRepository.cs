using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class KnowledgeChunkRepository(MeduzaDbContext db) : IKnowledgeChunkRepository
{
    /// <summary>
    /// Busca semântica por cosine distance com herança de escopo.
    /// Usa operador pgvector <=> para distância cosine.
    /// </summary>
    public async Task<List<KnowledgeChunkSearchResult>> SearchSemanticAsync(
        Vector queryEmbedding,
        Guid? clientId,
        Guid? siteId,
        int limit = 5,
        CancellationToken ct = default)
    {
        var chunksQuery = db.KnowledgeArticleChunks
            .Include(c => c.Article)
            .Where(c =>
                c.Embedding != null &&
                c.Article.DeletedAt == null &&
                c.Article.IsPublished);

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

        var results = await chunksQuery
            .OrderBy(c => c.Embedding!.CosineDistance(queryEmbedding))
            .Take(limit)
            .Select(c => new KnowledgeChunkSearchResult(
                c.ArticleId,
                c.Article.Title,
                c.Article.ClientId,
                c.Article.SiteId,
                c.SectionTitle,
                c.Content,
                (double)c.Embedding!.CosineDistance(queryEmbedding)))
            .ToListAsync(ct);

        return results;
    }

    public async Task<List<KnowledgeArticleChunk>> GetChunksWithoutEmbeddingAsync(
        int limit = 20,
        CancellationToken ct = default)
        => await db.KnowledgeArticleChunks
            .Include(c => c.Article)
            .Where(c => c.EmbeddingGeneratedAt == null && c.Article.IsPublished && c.Article.DeletedAt == null)
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
}
