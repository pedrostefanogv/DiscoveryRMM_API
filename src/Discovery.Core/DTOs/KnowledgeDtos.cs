using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

// ─── Request DTOs ──────────────────────────────────────────────

public record CreateArticleRequest(
    string Title,
    string Content,
    string? Category,
    List<string>? Tags,
    string? CreatedBy,
    Guid? ClientId,
    Guid? SiteId,
    Guid? DepartmentId = null);

public record UpdateArticleRequest(
    string Title,
    string Content,
    string? Category,
    List<string>? Tags,
    string? LastEditedBy);

public record PublishArticleRequest(
    string Status,        // "Published" ou "Internal"
    string? LastEditedBy,
    string? ChangeSummary = null);

public record LinkTicketRequest(
    Guid ArticleId,
    string? LinkedBy,
    string? Note);

public record KbSearchRequest(
    string Query,
    Guid? ClientId,
    Guid? SiteId,
    Guid? DepartmentId = null,
    string Mode = "hybrid", // "semantic", "keyword", "hybrid"
    string? ScopeMode = null,  // null/omitido = legado (usa clientId/siteId), "all-visible" = multi-escopo via ACL
    int MaxResults = 10);

// ─── Response DTOs ─────────────────────────────────────────────

public record ArticleListItem(
    Guid Id,
    string Title,
    string? Category,
    List<string> Tags,
    string? CreatedBy,
    string? LastEditedBy,
    string Status,
    string Scope,           // "Global", "Client", "Site"
    string ScopeOrigin,     // "global", "client", "site" — origem real do escopo
    Guid? ClientId,
    Guid? SiteId,
    string? ClientName,     // resolvido via join, null para globais
    string? SiteName,       // resolvido via join, null para globais ou client-level
    Guid? DepartmentId,
    int CurrentVersionNumber,
    DateTime? PublishedAt,
    int ChunkCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Resposta paginada (cursor-based) para listagem de artigos.</summary>
[Obsolete("Substituído por CursorPageDto<ArticleListItem>. Remover na v2.")]
public record ArticleListPage(
    IReadOnlyList<ArticleListItem> Items,
    int Count,
    string? Cursor,         // cursor anterior (para voltar)
    string? NextCursor,     // próximo cursor
    bool HasMore,
    int Limit);

public record ArticleResponse(
    Guid Id,
    string Title,
    string Content,
    string? Category,
    List<string> Tags,
    string? CreatedBy,
    string? LastEditedBy,
    DateTime? LastEditedAt,
    string Status,
    string Scope,
    string ScopeOrigin,
    Guid? ClientId,
    Guid? SiteId,
    string? ClientName,
    string? SiteName,
    Guid? DepartmentId,
    int CurrentVersionNumber,
    DateTime? PublishedAt,
    int ChunkCount,
    bool EmbeddingsReady,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ArticleVersionResponse(
    Guid Id,
    Guid ArticleId,
    int VersionNumber,
    string Title,
    string Content,
    string? Category,
    List<string> Tags,
    string Status,
    string? EditedBy,
    string? ChangeSummary,
    DateTime CreatedAt);

// ─── Sub-páginas internas do artigo (estilo Notion) ────────────────

/// <summary>Request para criar uma sub-página interna em um artigo.</summary>
public record CreateArticlePageRequest(
    string Title,
    string Content,
    Guid? ParentPageId = null,
    int SortOrder = 0);

/// <summary>Request para atualizar uma sub-página interna.</summary>
public record UpdateArticlePageRequest(
    string Title,
    string Content,
    Guid? ParentPageId = null,
    int SortOrder = 0);

/// <summary>Sub-página interna de um artigo (nó da árvore).</summary>
public record ArticlePageResponse(
    Guid Id,
    Guid ArticleId,
    Guid? ParentPageId,
    string Title,
    string Content,
    int SortOrder,
    int ChildCount,
    IReadOnlyList<ArticlePageResponse> Children,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Nó da árvore de sub-páginas internas de um artigo (estilo Notion).
/// Representa uma parte/página DENTRO do artigo e suas sub-páginas aninhadas (até 3 níveis).
/// </summary>
public record ArticlePageTreeNode(
    Guid Id,
    Guid ArticleId,
    Guid? ParentPageId,
    string Title,
    int SortOrder,
    int ChildCount,
    IReadOnlyList<ArticlePageTreeNode> Children);

/// <summary>
/// Nó da árvore de páginas da base de conhecimento (estilo Notion).
/// Representa uma página e suas subpáginas aninhadas (até 3 níveis).
/// </summary>
public record KnowledgeTreeNode(
    Guid Id,
    string Title,
    string? Category,
    string Status,
    string Scope,
    string ScopeOrigin,
    Guid? ClientId,
    Guid? SiteId,
    Guid? DepartmentId,
    Guid? ParentId,
    int SortOrder,
    bool IsPage,
    int ChildCount,
    IReadOnlyList<KnowledgeTreeNode> Children);

public record KbSearchResult(
    Guid ArticleId,
    string ArticleTitle,
    string? SectionTitle,
    string Excerpt,          // Trecho relevante do chunk
    string? Category,
    string Scope,
    string ScopeOrigin,      // "global", "client", "site"
    Guid? ClientId,
    Guid? SiteId,
    string? ClientName,
    string? SiteName,
    double? Score);          // Cosine similarity (0–1), null para resultado keyword

public record TicketKnowledgeLinkResponse(
    Guid LinkId,
    Guid TicketId,
    Guid ArticleId,
    string ArticleTitle,
    string? Category,
    string? LinkedBy,
    string? Note,
    DateTime LinkedAt);

public record KbSuggestResult(
    List<KbSearchResult> Suggestions);

/// <summary>Dados brutos de página usados pelo repositório (antes de mapear para DTO).</summary>
public class ArticleListPageData
{
    public IReadOnlyList<KnowledgeArticle> Items { get; init; } = [];
    public int Count { get; init; }
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>DTO plano para envio ao agent, sem navigation properties que causam recursão infinita.</summary>
public record AgentKnowledgeArticleDto(
    Guid Id,
    string Title,
    string Content,
    string? Category,
    List<string> Tags,
    string? TagsJson,
    string Status,
    string? CreatedBy,
    string? LastEditedBy,
    DateTime? LastEditedAt,
    Guid? ClientId,
    Guid? SiteId,
    Guid? DepartmentId,
    int CurrentVersionNumber,
    DateTime? PublishedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);
