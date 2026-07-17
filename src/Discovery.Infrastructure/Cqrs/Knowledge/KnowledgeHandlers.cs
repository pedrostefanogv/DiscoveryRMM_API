using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.Cqrs.Knowledge.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Knowledge;

public sealed class SearchKnowledgeQueryHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<SearchKnowledgeQuery, Result<IReadOnlyList<ArticleResponse>>>
{
    public async Task<Result<IReadOnlyList<ArticleResponse>>> Handle(SearchKnowledgeQuery q, CancellationToken ct)
    {
        var articles = await repo.SearchKeywordAsync(q.Query, q.ClientId, q.SiteId, null, ct);
        var dtos = articles.Select(MapToResponse).ToList();
        return Result<IReadOnlyList<ArticleResponse>>.Success(dtos);
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a) => new(
        Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
        Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt, Status: a.Status,
        Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
        ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
        DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt, ChunkCount: a.Chunks?.Count ?? 0,
        EmbeddingsReady: a.LastChunkedAt.HasValue,
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt);

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

public sealed class ListKnowledgeArticlesQueryHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<ListKnowledgeArticlesQuery, Result<IReadOnlyList<ArticleResponse>>>
{
    public async Task<Result<IReadOnlyList<ArticleResponse>>> Handle(ListKnowledgeArticlesQuery q, CancellationToken ct)
    {
        var articles = await repo.ListByScopeAsync(q.ClientId, q.SiteId, null, null, null, ct);
        var dtos = articles.Select(MapToResponse).ToList();
        return Result<IReadOnlyList<ArticleResponse>>.Success(dtos);
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a) => new(
        Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
        Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt, Status: a.Status,
        Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
        ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
        DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt, ChunkCount: a.Chunks?.Count ?? 0,
        EmbeddingsReady: a.LastChunkedAt.HasValue,
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt);

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

public sealed class ListKnowledgeArticlesByUserScopeQueryHandler(
    IKnowledgeArticleRepository repo,
    IScopeContext scopeContext
) : IRequestHandler<ListKnowledgeArticlesByUserScopeQuery, Result<ArticleListPage>>
{
    public async Task<Result<ArticleListPage>> Handle(ListKnowledgeArticlesByUserScopeQuery q, CancellationToken ct)
    {
        var scope = await scopeContext.GetAccessAsync(ResourceType.KnowledgeBase, ActionType.View);
        var hasGlobal = scope.HasGlobalAccess;
        var allowedClientIds = scope.AllowedClientIds.ToHashSet();
        var allowedSiteIds = scope.AllowedSiteIds.ToHashSet();

        var data = await repo.ListByUserScopeAsync(
            hasGlobal, allowedClientIds, allowedSiteIds,
            q.Status, q.DepartmentId, q.Category, q.Cursor, q.Limit, ct);

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
            ChunkCount: a.Chunks?.Count ?? 0,
            CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt
        )).ToList();

        return Result<ArticleListPage>.Success(new ArticleListPage(
            Items: items,
            Count: data.Count,
            Cursor: null,             // cursor da página atual (não usamos cursor reverso)
            NextCursor: data.NextCursor,
            HasMore: data.HasMore,
            Limit: q.Limit
        ));
    }
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

    private static ArticleResponse MapToResponse(KnowledgeArticle a) => new(
        Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
        Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt, Status: a.Status,
        Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
        ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
        DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt, ChunkCount: a.Chunks?.Count ?? 0,
        EmbeddingsReady: a.LastChunkedAt.HasValue,
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt);

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
            Status = ArticleStatus.Draft.ToString()
        };

        var created = await repo.CreateAsync(article, ct);
        return Result<ArticleResponse>.Success(MapToResponse(created));
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a) => new(
        Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
        Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt, Status: a.Status,
        Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
        ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
        DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt, ChunkCount: a.Chunks?.Count ?? 0,
        EmbeddingsReady: a.LastChunkedAt.HasValue,
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt);

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

public sealed class UpdateKnowledgeArticleCommandHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<UpdateKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(UpdateKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = await repo.GetByIdAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        article.Title = cmd.Title;
        article.Content = cmd.Content;
        article.Category = cmd.Category;
        article.TagsJson = cmd.Tags is { Count: > 0 } ? JsonSerializer.Serialize(cmd.Tags) : null;
        article.LastEditedBy = cmd.LastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;

        var updated = await repo.UpdateAsync(article, ct);
        return Result<ArticleResponse>.Success(MapToResponse(updated));
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a) => new(
        Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
        Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt, Status: a.Status,
        Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
        ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
        DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt, ChunkCount: a.Chunks?.Count ?? 0,
        EmbeddingsReady: a.LastChunkedAt.HasValue,
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt);

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

public sealed class PublishKnowledgeArticleCommandHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<PublishKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(PublishKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = await repo.GetByIdAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        if (cmd.Status != ArticleStatus.Published.ToString() && cmd.Status != ArticleStatus.Internal.ToString())
            return Result<ArticleResponse>.Failure(Error.Validation("Status", $"Status inválido: {cmd.Status}. Use 'Published' ou 'Internal'."));

        article.Status = cmd.Status;
        article.LastEditedBy = cmd.LastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;
        article.PublishedAt ??= DateTime.UtcNow;
        article.CurrentVersionNumber++;

        var updated = await repo.UpdateAsync(article, ct);

        return Result<ArticleResponse>.Success(new ArticleResponse(
            Id: updated.Id, Title: updated.Title, Content: updated.Content, Category: updated.Category,
            Tags: [], CreatedBy: updated.CreatedBy, LastEditedBy: updated.LastEditedBy,
            LastEditedAt: updated.LastEditedAt, Status: updated.Status,
            Scope: "Global", ScopeOrigin: "global",
            ClientId: updated.ClientId, SiteId: updated.SiteId, ClientName: null, SiteName: null,
            DepartmentId: updated.DepartmentId, CurrentVersionNumber: updated.CurrentVersionNumber,
            PublishedAt: updated.PublishedAt, ChunkCount: 0, EmbeddingsReady: false,
            CreatedAt: updated.CreatedAt, UpdatedAt: updated.UpdatedAt));
    }
}

public sealed class UnpublishKnowledgeArticleCommandHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<UnpublishKnowledgeArticleCommand, Result<ArticleResponse>>
{
    public async Task<Result<ArticleResponse>> Handle(UnpublishKnowledgeArticleCommand cmd, CancellationToken ct)
    {
        var article = await repo.GetByIdAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        article.Status = ArticleStatus.Draft.ToString();
        article.LastEditedBy = cmd.LastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;

        var updated = await repo.UpdateAsync(article, ct);

        return Result<ArticleResponse>.Success(new ArticleResponse(
            Id: updated.Id, Title: updated.Title, Content: updated.Content, Category: updated.Category,
            Tags: [], CreatedBy: updated.CreatedBy, LastEditedBy: updated.LastEditedBy,
            LastEditedAt: updated.LastEditedAt, Status: updated.Status,
            Scope: "Global", ScopeOrigin: "global",
            ClientId: updated.ClientId, SiteId: updated.SiteId, ClientName: null, SiteName: null,
            DepartmentId: updated.DepartmentId, CurrentVersionNumber: updated.CurrentVersionNumber,
            PublishedAt: updated.PublishedAt, ChunkCount: 0, EmbeddingsReady: false,
            CreatedAt: updated.CreatedAt, UpdatedAt: updated.UpdatedAt));
    }
}

public sealed class DeleteKnowledgeArticleCommandHandler(IKnowledgeArticleRepository repo)
    : IRequestHandler<DeleteKnowledgeArticleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteKnowledgeArticleCommand cmd, CancellationToken ct)
    {
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
            Tags: [], Status: v.Status, EditedBy: v.EditedBy,
            ChangeSummary: v.ChangeSummary, CreatedAt: v.CreatedAt)).ToList();
        return Result<IReadOnlyList<ArticleVersionResponse>>.Success(dtos);
    }
}
