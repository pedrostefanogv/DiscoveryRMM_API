using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório de sub-páginas internas de artigos da base de conhecimento (estilo Notion).
/// As sub-páginas pertencem a um único artigo e podem ser aninhadas (até 3 níveis).
/// </summary>
public interface IKnowledgeArticlePageRepository
{
    /// <summary>Obtém uma sub-página pelo id, garantindo que pertença ao artigo.</summary>
    Task<KnowledgeArticlePage?> GetByIdAsync(Guid articleId, Guid pageId, CancellationToken ct = default);

    /// <summary>Obtém uma sub-página incluindo o pai (para validação de profundidade).</summary>
    Task<KnowledgeArticlePage?> GetByIdWithParentAsync(Guid articleId, Guid pageId, CancellationToken ct = default);

    /// <summary>Lista todas as sub-páginas de um artigo (plano, para montagem da árvore).</summary>
    Task<List<KnowledgeArticlePage>> ListByArticleAsync(Guid articleId, CancellationToken ct = default);

    /// <summary>Cria uma sub-página.</summary>
    Task<KnowledgeArticlePage> CreateAsync(KnowledgeArticlePage page, CancellationToken ct = default);

    /// <summary>Atualiza uma sub-página.</summary>
    Task<KnowledgeArticlePage> UpdateAsync(KnowledgeArticlePage page, CancellationToken ct = default);

    /// <summary>Exclui uma sub-página e toda a sua subárvore.</summary>
    Task DeleteAsync(Guid articleId, Guid pageId, CancellationToken ct = default);

    /// <summary>Calcula a profundidade de uma sub-página pai (0 = nível 1, 1 = nível 2, ...).</summary>
    Task<int> GetDepthAsync(Guid? parentPageId, CancellationToken ct = default);

    /// <summary>Retorna o nível máximo (1-based) da subárvore enraizada em <paramref name="pageId"/>.</summary>
    Task<int> GetSubtreeMaxLevelAsync(Guid pageId, CancellationToken ct = default);

    /// <summary>Verifica se <paramref name="nodeId"/> é descendente de <paramref name="ancestorId"/> (impede ciclos).</summary>
    Task<bool> IsDescendantAsync(Guid ancestorId, Guid nodeId, CancellationToken ct = default);
}
