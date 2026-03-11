namespace Meduza.Core.DTOs;

// ─── Request DTOs ──────────────────────────────────────────────

public record CreateArticleRequest(
    string Title,
    string Content,
    string? Category,
    List<string>? Tags,
    string? Author,
    Guid? ClientId,
    Guid? SiteId);

public record UpdateArticleRequest(
    string Title,
    string Content,
    string? Category,
    List<string>? Tags,
    string? Author);

public record LinkTicketRequest(
    Guid ArticleId,
    string? LinkedBy,
    string? Note);

public record KbSearchRequest(
    string Query,
    Guid? ClientId,
    Guid? SiteId,
    string Mode = "hybrid", // "semantic", "keyword", "hybrid"
    int MaxResults = 10);

// ─── Response DTOs ─────────────────────────────────────────────

public record ArticleListItem(
    Guid Id,
    string Title,
    string? Category,
    List<string> Tags,
    string? Author,
    string Scope,           // "Global", "Client", "Site"
    Guid? ClientId,
    Guid? SiteId,
    bool IsPublished,
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
    string? Author,
    string Scope,
    Guid? ClientId,
    Guid? SiteId,
    bool IsPublished,
    DateTime? PublishedAt,
    int ChunkCount,
    bool EmbeddingsReady,
    DateTime CreatedAt,
    DateTime UpdatedAt);

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
