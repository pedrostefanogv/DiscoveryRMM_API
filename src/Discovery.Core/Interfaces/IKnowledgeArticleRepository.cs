using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IKnowledgeArticleRepository
{
    Task<KnowledgeArticle?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<KnowledgeArticle> CreateAsync(KnowledgeArticle article, CancellationToken ct = default);
    Task<KnowledgeArticle> UpdateAsync(KnowledgeArticle article, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default); // soft delete

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
    /// Busca por palavra-chave em title + content + tags (ILIKE)
    /// </summary>
    Task<List<KnowledgeArticle>> SearchKeywordAsync(
        string query,
        Guid? clientId,
        Guid? siteId,
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
