using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IKnowledgeChunkingService
{
    /// <summary>
    /// Divide um artigo em chunks por seção Markdown (estratégia padrão: semantic).
    /// </summary>
    List<KnowledgeArticleChunk> ChunkArticle(KnowledgeArticle article);

    /// <summary>
    /// Divide um artigo com estratégia configurável: "semantic" (headers), "paragraph", "fixed".
    /// </summary>
    List<KnowledgeArticleChunk> ChunkArticleWithStrategy(
        KnowledgeArticle article,
        string strategy,
        int chunkSizeTokens,
        int overlapTokens);
}
