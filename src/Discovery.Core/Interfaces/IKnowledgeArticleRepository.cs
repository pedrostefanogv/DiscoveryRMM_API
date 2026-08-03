using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IKnowledgeArticleRepository
{
    Task<KnowledgeArticle?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<KnowledgeArticle> CreateAsync(KnowledgeArticle article, CancellationToken ct = default);
    Task<KnowledgeArticle> UpdateAsync(KnowledgeArticle article, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default); // soft delete

    /// <summary>
    /// Obtém um artigo incluindo o pai (para resolver herança de escopo/status).
    /// </summary>
    Task<KnowledgeArticle?> GetByIdWithParentAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Sobe a cadeia de pais até a raiz e retorna o artigo raiz.
    /// Usado para herdar escopo/status em subpáginas.
    /// </summary>
    Task<KnowledgeArticle?> GetRootAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Calcula a profundidade de um nó pai (0 = raiz, 1 = filho da raiz, ...).
    /// Usado para validar o limite de 3 níveis de profundidade.
    /// </summary>
    Task<int> GetDepthAsync(Guid? parentId, CancellationToken ct = default);

    /// <summary>
    /// Lista todos os artigos visíveis (ACL multi-escopo) para montagem da árvore de páginas.
    /// Retorna a lista plana; a montagem da árvore é feita no handler.
    /// </summary>
    Task<List<KnowledgeArticle>> ListForTreeAsync(
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        string? status = null,
        Guid? departmentId = null,
        string? category = null,
        Guid? filterClientId = null,
        Guid? filterSiteId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lista artigos respeitando herança de escopo:
    /// site → client → global (todos os níveis superiores são incluídos)
    /// + filtro por status e departamento
    /// </summary>
    Task<List<KnowledgeArticle>> ListByScopeAsync(
        Guid? clientId,
        Guid? siteId,
        string? status = null,
        Guid? departmentId = null,
        string? category = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lista artigos de múltiplos escopos (ACL do usuário) com paginação cursor-based.
    /// Quando <paramref name="allowedClientIds"/> e <paramref name="allowedSiteIds"/> estão vazios
    /// e <paramref name="hasGlobalAccess"/> é false, retorna apenas artigos globais (client_id IS NULL, site_id IS NULL).
    /// <paramref name="filterClientId"/> e <paramref name="filterSiteId"/> refinam o resultado
    /// (ex.: selecionar um cliente mostra globais + artigos daquele cliente).
    /// </summary>
    Task<ArticleListPageData> ListByUserScopeAsync(
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        string? status = null,
        Guid? departmentId = null,
        string? category = null,
        string? cursor = null,
        int limit = 20,
        Guid? filterClientId = null,
        Guid? filterSiteId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Busca por palavra-chave em title + content + tags (ILIKE)
    /// </summary>
    Task<List<KnowledgeArticle>> SearchKeywordAsync(
        string query,
        Guid? clientId,
        Guid? siteId,
        Guid? departmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Busca por palavra-chave em múltiplos escopos (ACL do usuário).
    /// </summary>
    Task<List<KnowledgeArticle>> SearchKeywordByUserScopeAsync(
        string query,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        Guid? departmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Artigos publicados/internos que precisam re-chunking (last_chunked_at IS NULL ou anterior a updated_at)
    /// </summary>
    Task<List<KnowledgeArticle>> GetArticlesNeedingChunkingAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Artigos linkados a um ticket
    /// </summary>
    Task<List<KnowledgeArticle>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>
    /// Verifica rapidamente se existem artigos publicados/internos no escopo.
    /// Usado como guard clause para evitar chamadas de embedding quando a KB está vazia.
    /// </summary>
    Task<bool> HasPublishedArticlesAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default);

    // ─── Versionamento ──────────────────────────────────────────

    /// <summary>
    /// Cria snapshot de versão ao publicar/internalizar
    /// </summary>
    Task<KnowledgeArticleVersion> CreateVersionAsync(KnowledgeArticleVersion version, CancellationToken ct = default);

    /// <summary>
    /// Lista versões de um artigo (decrescente por version_number)
    /// </summary>
    Task<List<KnowledgeArticleVersion>> GetVersionsAsync(Guid articleId, CancellationToken ct = default);

    /// <summary>
    /// Obtém uma versão específica
    /// </summary>
    Task<KnowledgeArticleVersion?> GetVersionAsync(Guid articleId, int versionNumber, CancellationToken ct = default);

    // ─── Ticket ↔ KB ───────────────────────────────────────────

    Task<TicketKnowledgeLink?> GetLinkAsync(Guid ticketId, Guid articleId, CancellationToken ct = default);
    Task<TicketKnowledgeLink> LinkToTicketAsync(Guid ticketId, Guid articleId, string? linkedBy, string? note, CancellationToken ct = default);
    Task UnlinkFromTicketAsync(Guid ticketId, Guid articleId, CancellationToken ct = default);
    Task<List<TicketKnowledgeLink>> GetTicketLinksAsync(Guid ticketId, CancellationToken ct = default);
    Task<TicketKnowledgeLink> UpdateLinkAsync(TicketKnowledgeLink link, CancellationToken ct = default);
}
