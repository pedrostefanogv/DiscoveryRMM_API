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
    string Status,
    int CurrentVersionNumber,
    DateTime? PublishedAt,
    DateTime? LastEditedAt);
