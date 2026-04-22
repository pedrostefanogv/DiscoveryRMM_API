using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IKnowledgeChunkingService
{
    /// <summary>
    /// Divide um artigo em chunks por seção Markdown.
    /// Retorna lista ordenada de chunks (sem Id, sem Embedding — preenchidos posteriormente).
    /// </summary>
    List<KnowledgeArticleChunk> ChunkArticle(KnowledgeArticle article);
}
