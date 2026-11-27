using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.Cqrs.Knowledge.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.ValueObjects;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Knowledge;

public sealed class SearchKnowledgeQueryHandler(
    IKnowledgeSearchService searchService
) : IRequestHandler<SearchKnowledgeQuery, Result<IReadOnlyList<ArticleResponse>>>
{
    public async Task<Result<IReadOnlyList<ArticleResponse>>> Handle(SearchKnowledgeQuery q, CancellationToken ct)
    {
        // Busca unificada: semantic/keyword/hybrid com ACL multi-escopo do usuário
        // (ou escopo legado clientId/siteId quando informados).
        var hits = await searchService.SearchAsync(new KnowledgeSearchRequest(
            q.Query, q.Mode,
            UseUserScope: !q.ClientId.HasValue && !q.SiteId.HasValue,
            q.ClientId, q.SiteId, q.DepartmentId, q.MaxResults), ct);

        var dtos = hits.Select(h => MapToResponse(h.Article)).ToList();
        return Result<IReadOnlyList<ArticleResponse>>.Success(dtos);
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a)
    {
        var chunkCount = a.AiIndexingEnabled ? (a.Chunks?.Count ?? 0) : 0;
        var embeddedCount = a.AiIndexingEnabled ? (a.Chunks?.Count(c => c.EmbeddingGeneratedAt.HasValue) ?? 0) : 0;
        return new(
            Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
            Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
            LastEditedAt: a.LastEditedAt, Status: a.Status,
            Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
            ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
            DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
            PublishedAt: a.PublishedAt, ChunkCount: chunkCount,
            EmbeddingsReady: a.AiIndexingEnabled && chunkCount > 0 && embeddedCount == chunkCount,
            CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
            AiIndexingEnabled: a.AiIndexingEnabled, EmbeddedChunkCount: embeddedCount);
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ResolveScope(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string ResolveScopeOrigin(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "global",
            (not null, null) => "client",
            _ => "site"
        };
}

/// <summary>
/// POST /knowledge/chat-search — sugestões com score para IA/chat/feedback.
/// Antes esse endpoint era chamado pelo front sem existir na API (404).
/// </summary>
public sealed class SearchKbSuggestionsQueryHandler(IKnowledgeSearchService searchService)
    : IRequestHandler<SearchKbSuggestionsQuery, Result<KbSuggestResult>>
{
    public async Task<Result<KbSuggestResult>> Handle(SearchKbSuggestionsQuery q, CancellationToken ct)
    {
        var req = q.Request;
        var useUserScope = req.ScopeMode == "all-visible" || (!req.ClientId.HasValue && !req.SiteId.HasValue);

        var hits = await searchService.SearchAsync(new KnowledgeSearchRequest(
            req.Query, req.Mode, useUserScope, req.ClientId, req.SiteId, req.DepartmentId, req.MaxResults), ct);

        return Result<KbSuggestResult>.Success(new KbSuggestResult(BuildSuggestions(hits)));
    }

    private static List<KbSearchResult> BuildSuggestions(List<KnowledgeSearchHit> hits)
    {
        return hits.Select(h =>
        {
            // Sem os tokens de imagem: o excerpt vai para a IA/chat e não deve
            // carregar o identificador interno do anexo (chunks antigos, indexados
            // antes da limpeza, ainda podem conter o token).
            var excerptSource = KnowledgeImageMarkdown.StripTokens(
                h.Chunk is not null ? h.Chunk.ChunkContent : h.Article.Content);
            var excerpt = excerptSource.Length > 300 ? excerptSource[..300] + "..." : excerptSource;

            return new KbSearchResult(
                h.Article.Id, h.Article.Title,
                h.Chunk?.SectionTitle, excerpt, h.Article.Category,
                ResolveScope(h.Article.ClientId, h.Article.SiteId),
                ResolveScopeOrigin(h.Article.ClientId, h.Article.SiteId),
                h.Article.ClientId, h.Article.SiteId, null, null, h.Score);
        }).ToList();
    }

    private static string ResolveScope(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string ResolveScopeOrigin(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "global",
            (not null, null) => "client",
            _ => "site"
        };
}

public sealed class ListKnowledgeArticlesByUserScopeQueryHandler(
    IKnowledgeArticleRepository repo,
    IScopeContext scopeContext
) : IRequestHandler<ListKnowledgeArticlesByUserScopeQuery, Result<CursorPageDto<ArticleListItem>>>
{
    public async Task<Result<CursorPageDto<ArticleListItem>>> Handle(ListKnowledgeArticlesByUserScopeQuery q, CancellationToken ct)
    {
        var scope = await scopeContext.GetAccessAsync(ResourceType.KnowledgeBase, ActionType.View);
        var hasGlobal = scope.HasGlobalAccess;
        var allowedClientIds = scope.AllowedClientIds.ToHashSet();
        var allowedSiteIds = scope.AllowedSiteIds.ToHashSet();

        var data = await repo.ListByUserScopeAsync(
            hasGlobal, allowedClientIds, allowedSiteIds,
            q.Status, q.DepartmentId, q.Category, q.Cursor, q.Limit,
            q.ClientId, q.SiteId, q.SortBy, q.SortDirection, ct);

        var items = data.Items.Select(a => new ArticleListItem(
            Id: a.Id, Title: a.Title, Category: a.Category,
            Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
            Status: a.Status,
            Scope: ResolveScope(a.ClientId, a.SiteId),
            ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
            ClientId: a.ClientId, SiteId: a.SiteId,
            ClientName: null, SiteName: null,
            DepartmentId: a.DepartmentId,
            CurrentVersionNumber: a.CurrentVersionNumber,
            PublishedAt: a.PublishedAt,
            ChunkCount: data.ChunkCounts.TryGetValue(a.Id, out var chunkCount) ? chunkCount : 0,
            CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
            AiIndexingEnabled: a.AiIndexingEnabled,
            EmbeddedChunkCount: data.EmbeddedChunkCounts.TryGetValue(a.Id, out var embCount) ? embCount : 0
        )).ToList();

        return Result<CursorPageDto<ArticleListItem>>.Success(new CursorPageDto<ArticleListItem>(
            Items: items,
            ReturnedItems: items.Count,
            Cursor: null,
            NextCursor: data.NextCursor,
            HasMore: data.HasMore,
            Limit: q.Limit
        ));
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ResolveScope(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string ResolveScopeOrigin(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "global",
            (not null, null) => "client",
            _ => "site"
        };
}

public sealed class GetKnowledgeArticleByIdQueryHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<GetKnowledgeArticleByIdQuery, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(GetKnowledgeArticleByIdQuery q, CancellationToken ct)
    {
        var a = await repo.GetByIdAsync(q.Id, ct);
        if (a is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {q.Id} not found"));
        return Result<ArticleResponse>.Success(MapToResponse(a));
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a)
    {
        var chunkCount = a.AiIndexingEnabled ? (a.Chunks?.Count ?? 0) : 0;
        var embeddedCount = a.AiIndexingEnabled ? (a.Chunks?.Count(c => c.EmbeddingGeneratedAt.HasValue) ?? 0) : 0;
        return new(
            Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
            Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
            LastEditedAt: a.LastEditedAt, Status: a.Status,
            Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
            ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
            DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
            PublishedAt: a.PublishedAt, ChunkCount: chunkCount,
            EmbeddingsReady: a.AiIndexingEnabled && chunkCount > 0 && embeddedCount == chunkCount,
            CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
            AiIndexingEnabled: a.AiIndexingEnabled, EmbeddedChunkCount: embeddedCount);
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ResolveScope(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string ResolveScopeOrigin(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "global",
            (not null, null) => "client",
            _ => "site"
        };
}

// ── Command Handlers ──────────────────────────────────────────────────

public sealed class CreateKnowledgeArticleCommandHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<CreateKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(CreateKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = new KnowledgeArticle
        {
            Title = cmd.Title,
            Content = cmd.Content,
            Category = cmd.Category,
            TagsJson = cmd.Tags is { Count: > 0 } ? JsonSerializer.Serialize(cmd.Tags) : null,
            CreatedBy = cmd.CreatedBy,
            LastEditedBy = cmd.CreatedBy,
            ClientId = cmd.ClientId,
            SiteId = cmd.SiteId,
            DepartmentId = cmd.DepartmentId,
            Status = ArticleStatus.Draft.ToString(),
            AiIndexingEnabled = cmd.AiIndexingEnabled
        };

        var created = await repo.CreateAsync(article, ct);
        return Result<ArticleResponse>.Success(MapToResponse(created));
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a)
    {
        var chunkCount = a.AiIndexingEnabled ? (a.Chunks?.Count ?? 0) : 0;
        var embeddedCount = a.AiIndexingEnabled ? (a.Chunks?.Count(c => c.EmbeddingGeneratedAt.HasValue) ?? 0) : 0;
        return new(
            Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
            Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
            LastEditedAt: a.LastEditedAt, Status: a.Status,
            Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
            ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
            DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
            PublishedAt: a.PublishedAt, ChunkCount: chunkCount,
            EmbeddingsReady: a.AiIndexingEnabled && chunkCount > 0 && embeddedCount == chunkCount,
            CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
            AiIndexingEnabled: a.AiIndexingEnabled, EmbeddedChunkCount: embeddedCount);
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ResolveScope(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string ResolveScopeOrigin(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "global",
            (not null, null) => "client",
            _ => "site"
        };
}

public sealed class UpdateKnowledgeArticleCommandHandler(
    IKnowledgeArticleRepository repo,
    IKnowledgeChunkRepository chunkRepo,
    IKnowledgeEmbeddingQueueRepository queueRepo)
    : IRequestHandler<UpdateKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(UpdateKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = await repo.GetByIdAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        if (cmd.UpdateScope)
        {
            if (cmd.SiteId.HasValue && !cmd.ClientId.HasValue)
                return Result<ArticleResponse>.Failure(Error.Validation("SiteId", "ClientId é obrigatório quando SiteId é informado."));

            article.ClientId = cmd.ClientId;
            article.SiteId = cmd.SiteId;
            article.DepartmentId = cmd.DepartmentId;
        }

        var contentChanged = !string.Equals(article.Title, cmd.Title, StringComparison.Ordinal)
            || !string.Equals(article.Content, cmd.Content, StringComparison.Ordinal)
            || !string.Equals(article.Category, cmd.Category, StringComparison.Ordinal);

        article.Title = cmd.Title;
        article.Content = cmd.Content;
        article.Category = cmd.Category;
        article.TagsJson = cmd.Tags is { Count: > 0 } ? JsonSerializer.Serialize(cmd.Tags) : null;
        article.LastEditedBy = cmd.LastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;

        var prevAiEnabled = article.AiIndexingEnabled;
        if (cmd.AiIndexingEnabled.HasValue)
            article.AiIndexingEnabled = cmd.AiIndexingEnabled.Value;

        if (article.Status == ArticleStatus.AiOnly.ToString())
            article.AiIndexingEnabled = true;

        var isPublishedLike = article.Status != ArticleStatus.Draft.ToString();
        if (!article.AiIndexingEnabled)
        {
            await chunkRepo.ReplaceAllForArticleAsync(article.Id, [], ct);
            article.Chunks?.Clear();
            article.LastChunkedAt = null;
        }
        else if (isPublishedLike && (contentChanged || cmd.UpdateScope || !prevAiEnabled))
        {
            article.LastChunkedAt = null;
        }

        var updated = await repo.UpdateAsync(article, ct);

        if (isPublishedLike && updated.AiIndexingEnabled && (contentChanged || cmd.UpdateScope || !prevAiEnabled))
            await queueRepo.EnqueueAsync(updated.Id, "article_updated", ct);

        return Result<ArticleResponse>.Success(MapToResponse(updated));
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a)
    {
        var chunkCount = a.AiIndexingEnabled ? (a.Chunks?.Count ?? 0) : 0;
        var embeddedCount = a.AiIndexingEnabled ? (a.Chunks?.Count(c => c.EmbeddingGeneratedAt.HasValue) ?? 0) : 0;
        return new(
            Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
            Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
            LastEditedAt: a.LastEditedAt, Status: a.Status,
            Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
            ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
            DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
            PublishedAt: a.PublishedAt, ChunkCount: chunkCount,
            EmbeddingsReady: a.AiIndexingEnabled && chunkCount > 0 && embeddedCount == chunkCount,
            CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
            AiIndexingEnabled: a.AiIndexingEnabled, EmbeddedChunkCount: embeddedCount);
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ResolveScope(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string ResolveScopeOrigin(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "global",
            (not null, null) => "client",
            _ => "site"
        };
}

public sealed class PublishKnowledgeArticleCommandHandler(
    IKnowledgeArticleRepository repo,
    IKnowledgeEmbeddingQueueRepository queueRepo)
    : IRequestHandler<PublishKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(PublishKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = await repo.GetByIdAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        if (cmd.Status != ArticleStatus.Published.ToString()
            && cmd.Status != ArticleStatus.Internal.ToString()
            && cmd.Status != ArticleStatus.AiOnly.ToString())
        {
            return Result<ArticleResponse>.Failure(Error.Validation("Status", $"Status inválido: {cmd.Status}. Use 'Published', 'Internal' ou 'AiOnly'."));
        }

        if (cmd.Status == ArticleStatus.Internal.ToString() && !article.DepartmentId.HasValue)
            return Result<ArticleResponse>.Failure(Error.Validation("DepartmentId", "Selecione um departamento para artigos Internos."));

        article.Status = cmd.Status;
        if (cmd.Status == ArticleStatus.AiOnly.ToString())
            article.AiIndexingEnabled = true;

        article.LastEditedBy = cmd.LastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;
        article.PublishedAt ??= DateTime.UtcNow;
        article.CurrentVersionNumber++;

        var updated = await repo.UpdateAsync(article, ct);

        await repo.CreateVersionAsync(new KnowledgeArticleVersion
        {
            ArticleId = updated.Id,
            VersionNumber = updated.CurrentVersionNumber,
            Title = updated.Title,
            Content = updated.Content,
            Category = updated.Category,
            TagsJson = updated.TagsJson,
            Status = updated.Status,
            EditedBy = cmd.LastEditedBy,
            ChangeSummary = cmd.ChangeSummary
        }, ct);

        if (updated.AiIndexingEnabled)
            await queueRepo.EnqueueAsync(updated.Id, "article_published", ct);

        var chunkCount = updated.Chunks?.Count ?? 0;
        var embeddedCount = updated.Chunks?.Count(c => c.EmbeddingGeneratedAt.HasValue) ?? 0;
        return Result<ArticleResponse>.Success(new ArticleResponse(
            Id: updated.Id, Title: updated.Title, Content: updated.Content, Category: updated.Category,
            Tags: ParseTags(updated.TagsJson), CreatedBy: updated.CreatedBy, LastEditedBy: updated.LastEditedBy,
            LastEditedAt: updated.LastEditedAt, Status: updated.Status,
            Scope: (updated.ClientId, updated.SiteId) switch { (null, null) => "Global", (not null, null) => "Client", _ => "Site" },
            ScopeOrigin: (updated.ClientId, updated.SiteId) switch { (null, null) => "global", (not null, null) => "client", _ => "site" },
            ClientId: updated.ClientId, SiteId: updated.SiteId, ClientName: null, SiteName: null,
            DepartmentId: updated.DepartmentId, CurrentVersionNumber: updated.CurrentVersionNumber,
            PublishedAt: updated.PublishedAt, ChunkCount: chunkCount,
            EmbeddingsReady: updated.AiIndexingEnabled && chunkCount > 0 && embeddedCount == chunkCount,
            CreatedAt: updated.CreatedAt, UpdatedAt: updated.UpdatedAt,
            AiIndexingEnabled: updated.AiIndexingEnabled, EmbeddedChunkCount: embeddedCount));
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}

public sealed class UnpublishKnowledgeArticleCommandHandler(
    IKnowledgeArticleRepository repo,
    IKnowledgeChunkRepository chunkRepo)
    : IRequestHandler<UnpublishKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(UnpublishKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = await repo.GetByIdAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        article.Status = ArticleStatus.Draft.ToString();
        article.LastEditedBy = cmd.LastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;
        article.LastChunkedAt = null;

        await chunkRepo.ReplaceAllForArticleAsync(article.Id, [], ct);
        var updated = await repo.UpdateAsync(article, ct);

        return Result<ArticleResponse>.Success(new ArticleResponse(
            Id: updated.Id, Title: updated.Title, Content: updated.Content, Category: updated.Category,
            Tags: ParseTags(updated.TagsJson), CreatedBy: updated.CreatedBy, LastEditedBy: updated.LastEditedBy,
            LastEditedAt: updated.LastEditedAt, Status: updated.Status,
            Scope: (updated.ClientId, updated.SiteId) switch { (null, null) => "Global", (not null, null) => "Client", _ => "Site" },
            ScopeOrigin: (updated.ClientId, updated.SiteId) switch { (null, null) => "global", (not null, null) => "client", _ => "site" },
            ClientId: updated.ClientId, SiteId: updated.SiteId, ClientName: null, SiteName: null,
            DepartmentId: updated.DepartmentId, CurrentVersionNumber: updated.CurrentVersionNumber,
            PublishedAt: updated.PublishedAt, ChunkCount: 0, EmbeddingsReady: false,
            CreatedAt: updated.CreatedAt, UpdatedAt: updated.UpdatedAt,
            AiIndexingEnabled: updated.AiIndexingEnabled, EmbeddedChunkCount: 0));
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}

public sealed class ReindexKnowledgeArticleCommandHandler(
    IKnowledgeArticleRepository repo,
    IKnowledgeChunkRepository chunkRepo,
    IKnowledgeEmbeddingQueueRepository queueRepo)
    : IRequestHandler<ReindexKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(ReindexKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = await repo.GetByIdAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        if (article.Status == ArticleStatus.Draft.ToString())
            return Result<ArticleResponse>.Failure(Error.Validation("Status", "Artigos em rascunho não são indexados pela IA."));

        if (!article.AiIndexingEnabled)
            return Result<ArticleResponse>.Failure(Error.Validation("AiIndexingEnabled", "Indexação por IA está desativada para este artigo."));

        await chunkRepo.ReplaceAllForArticleAsync(article.Id, [], ct);
        article.LastChunkedAt = null;
        var updated = await repo.UpdateAsync(article, ct);
        await queueRepo.EnqueueAsync(updated.Id, "manual_reindex", ct);

        return Result<ArticleResponse>.Success(new ArticleResponse(
            Id: updated.Id, Title: updated.Title, Content: updated.Content, Category: updated.Category,
            Tags: ParseTags(updated.TagsJson), CreatedBy: updated.CreatedBy, LastEditedBy: updated.LastEditedBy,
            LastEditedAt: updated.LastEditedAt, Status: updated.Status,
            Scope: (updated.ClientId, updated.SiteId) switch { (null, null) => "Global", (not null, null) => "Client", _ => "Site" },
            ScopeOrigin: (updated.ClientId, updated.SiteId) switch { (null, null) => "global", (not null, null) => "client", _ => "site" },
            ClientId: updated.ClientId, SiteId: updated.SiteId, ClientName: null, SiteName: null,
            DepartmentId: updated.DepartmentId, CurrentVersionNumber: updated.CurrentVersionNumber,
            PublishedAt: updated.PublishedAt, ChunkCount: 0, EmbeddingsReady: false,
            CreatedAt: updated.CreatedAt, UpdatedAt: updated.UpdatedAt,
            AiIndexingEnabled: updated.AiIndexingEnabled, EmbeddedChunkCount: 0));
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}

public sealed class GetKnowledgeCategoriesQueryHandler(
    IKnowledgeArticleRepository repo,
    IScopeContext scopeContext)
    : IRequestHandler<GetKnowledgeCategoriesQuery, Result<IReadOnlyList<string>>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(GetKnowledgeCategoriesQuery q, CancellationToken ct)
    {
        var scope = await scopeContext.GetAccessAsync(ResourceType.KnowledgeBase, ActionType.View);
        var categories = await repo.ListDistinctCategoriesByUserScopeAsync(
            scope.HasGlobalAccess,
            scope.AllowedClientIds.ToHashSet(),
            scope.AllowedSiteIds.ToHashSet(),
            ct);
        return Result<IReadOnlyList<string>>.Success(categories);
    }
}

public sealed class DeleteKnowledgeArticleCommandHandler(
    IKnowledgeArticleRepository repo,
    IAttachmentRepository attachmentRepository)
    : IRequestHandler<DeleteKnowledgeArticleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        // Imagens do artigo entram em soft delete junto com ele: o objeto no
        // storage é removido depois pelo job de retenção (PermanentlyDelete...).
        // Sem isso as imagens ficariam órfãs e acessíveis por quem guardou o id.
        await attachmentRepository.SoftDeleteByEntityAsync(KnowledgeImageRules.EntityType, cmd.Id, ct);

        await repo.DeleteAsync(cmd.Id, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class GetKnowledgeArticleVersionsQueryHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<GetKnowledgeArticleVersionsQuery, Result<IReadOnlyList<ArticleVersionResponse>>>
{
    public async Task<Result<IReadOnlyList<ArticleVersionResponse>>> Handle(GetKnowledgeArticleVersionsQuery q, CancellationToken ct)
    {
        var versions = await repo.GetVersionsAsync(q.ArticleId, ct);
        var dtos = versions.Select(v => new ArticleVersionResponse(
            Id: v.Id, ArticleId: v.ArticleId, VersionNumber: v.VersionNumber,
            Title: v.Title, Content: v.Content, Category: v.Category,
            Tags: ParseTags(v.TagsJson), Status: v.Status, EditedBy: v.EditedBy,
            ChangeSummary: v.ChangeSummary, CreatedAt: v.CreatedAt)).ToList();
        return Result<IReadOnlyList<ArticleVersionResponse>>.Success(dtos);
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
