using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IKnowledgeChunkingService
{
    /// <summary>
    /// Divide um artigo em chunks por seção Markdown (estratégia padrão: semantic).
    /// Quando <paramref name="pages"/> é informado, o conteúdo das sub-páginas
    /// (estilo Notion) é anexado ao artigo com o título da página como header —
    /// assim o conteúdo das páginas também é indexado e recuperável.
    /// </summary>
    List<KnowledgeArticleChunk> ChunkArticle(
        KnowledgeArticle article,
        IReadOnlyList<KnowledgeArticlePage>? pages = null);

    /// <summary>
    /// Divide um artigo com estratégia configurável: "semantic" (headers), "paragraph", "fixed".
    /// </summary>
    List<KnowledgeArticleChunk> ChunkArticleWithStrategy(
        KnowledgeArticle article,
        string strategy,
        int chunkSizeTokens,
        int overlapTokens);
}
