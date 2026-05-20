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
    Guid? ClientId,
    Guid? SiteId,
    Guid? DepartmentId,
    int CurrentVersionNumber,
    DateTime? PublishedAt,
    int ChunkCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

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
    Guid? ClientId,
    Guid? SiteId,
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
    Guid? ClientId,
    Guid? SiteId,
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
