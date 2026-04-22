using Pgvector;

namespace Discovery.Core.Entities;

/// <summary>
/// Chunk (trecho) de um artigo da KB, com embedding pgvector para busca semântica.
/// Cada artigo é dividido por seções Markdown (H2/H3); artigos curtos ficam em 1 chunk.
/// </summary>
public class KnowledgeArticleChunk
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }

    public int ChunkIndex { get; set; }          // Ordem dentro do artigo
    public string? SectionTitle { get; set; }    // Header Markdown da seção (ex: "## Procedimento")
    public string Content { get; set; } = string.Empty; // Texto plain do chunk (sem Markdown)
    public int TokenCount { get; set; }

    // pgvector — 1536 dimensões (text-embedding-3-small)
    public Vector? Embedding { get; set; }
    public DateTime? EmbeddingGeneratedAt { get; set; }

    public KnowledgeArticle Article { get; set; } = null!;
}
