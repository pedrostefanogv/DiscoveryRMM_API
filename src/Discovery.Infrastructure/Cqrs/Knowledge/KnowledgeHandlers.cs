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

public sealed class SearchKnowledgeQueryHandler(
    IKnowledgeArticleRepository repo,
    IScopeContext scopeContext
) : IRequestHandler<SearchKnowledgeQuery, Result<IReadOnlyList<ArticleResponse>>>
{
    public async Task<Result<IReadOnlyList<ArticleResponse>>> Handle(SearchKnowledgeQuery q, CancellationToken ct)
    {
        // Se o usuário selecionou um escopo específico (clientId/siteId), usa keyword search legado com escopo.
        // Caso contrário, usa busca multi-escopo via ACL do usuário.
        List<KnowledgeArticle> articles;

        if (q.ClientId.HasValue || q.SiteId.HasValue)
        {
            articles = await repo.SearchKeywordAsync(q.Query, q.ClientId, q.SiteId, null, ct);
        }
        else
        {
            var scope = await scopeContext.GetAccessAsync(ResourceType.KnowledgeBase, ActionType.View);
            articles = await repo.SearchKeywordByUserScopeAsync(
                q.Query,
                scope.HasGlobalAccess,
                scope.AllowedClientIds.ToHashSet(),
                scope.AllowedSiteIds.ToHashSet(),
                departmentId: null,
                ct: ct);
        }

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
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
        ParentId: a.ParentId, SortOrder: a.SortOrder, IsPage: a.IsPage);

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
            q.ClientId, q.SiteId, ct);

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
            CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
            ParentId: a.ParentId, SortOrder: a.SortOrder, IsPage: a.IsPage
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

    private static ArticleResponse MapToResponse(KnowledgeArticle a) => new(
        Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
        Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt, Status: a.Status,
        Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
        ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
        DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt, ChunkCount: a.Chunks?.Count ?? 0,
        EmbeddingsReady: a.LastChunkedAt.HasValue,
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
        ParentId: a.ParentId, SortOrder: a.SortOrder, IsPage: a.IsPage);

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

public sealed class GetKnowledgeTreeQueryHandler(
    IKnowledgeArticleRepository repo,
    IScopeContext scopeContext
) : IRequestHandler<GetKnowledgeTreeQuery, Result<IReadOnlyList<KnowledgeTreeNode>>>
{
    public async Task<Result<IReadOnlyList<KnowledgeTreeNode>>> Handle(GetKnowledgeTreeQuery q, CancellationToken ct)
    {
        var scope = await scopeContext.GetAccessAsync(ResourceType.KnowledgeBase, ActionType.View);
        var hasGlobal = scope.HasGlobalAccess;
        var allowedClientIds = scope.AllowedClientIds.ToHashSet();
        var allowedSiteIds = scope.AllowedSiteIds.ToHashSet();

        var articles = await repo.ListForTreeAsync(
            hasGlobal, allowedClientIds, allowedSiteIds,
            q.Status, q.DepartmentId, q.Category,
            q.ClientId, q.SiteId, ct);

        var roots = BuildTree(articles);
        return Result<IReadOnlyList<KnowledgeTreeNode>>.Success(roots);
    }

    /// <summary>
    /// Monta a árvore de páginas a partir da lista plana.
    /// Ordena irmãos por SortOrder (ascendente) e depois por título.
    /// </summary>
    private static IReadOnlyList<KnowledgeTreeNode> BuildTree(List<KnowledgeArticle> articles)
    {
        // Usa Guid.Empty como chave sentinela para páginas raiz (ParentId == null)
        var childrenMap = new Dictionary<Guid, List<KnowledgeArticle>>();
        foreach (var a in articles)
        {
            var key = a.ParentId ?? Guid.Empty;
            if (!childrenMap.TryGetValue(key, out var list))
            {
                list = new List<KnowledgeArticle>();
                childrenMap[key] = list;
            }
            list.Add(a);
        }

        // Ordena cada lista de irmãos
        foreach (var list in childrenMap.Values)
        {
            list.Sort((x, y) =>
            {
                var c = x.SortOrder.CompareTo(y.SortOrder);
                return c != 0 ? c : string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase);
            });
        }

        IReadOnlyList<KnowledgeTreeNode> Build(Guid? parentId, int depth = 0)
        {
            // Proteção contra ciclos/recursão infinita (máx. 3 níveis + folga)
            if (depth > 10) return [];

            var key = parentId ?? Guid.Empty;
            if (!childrenMap.TryGetValue(key, out var siblings))
                return [];

            var nodes = new List<KnowledgeTreeNode>(siblings.Count);
            foreach (var a in siblings)
            {
                var children = Build(a.Id, depth + 1);
                nodes.Add(new KnowledgeTreeNode(
                    Id: a.Id,
                    Title: a.Title,
                    Category: a.Category,
                    Status: a.Status,
                    Scope: ResolveScope(a.ClientId, a.SiteId),
                    ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
                    ClientId: a.ClientId,
                    SiteId: a.SiteId,
                    DepartmentId: a.DepartmentId,
                    ParentId: a.ParentId,
                    SortOrder: a.SortOrder,
                    IsPage: a.IsPage,
                    ChildCount: children.Count,
                    Children: children));
            }
            return nodes;
        }

        return Build(null);
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
        // Validação de hierarquia: se houver página pai, valida profundidade e herda escopo/status
        Guid? clientId = cmd.ClientId;
        Guid? siteId = cmd.SiteId;
        Guid? departmentId = cmd.DepartmentId;
        string status = ArticleStatus.Draft.ToString();

        if (cmd.ParentId.HasValue)
        {
            var parent = await repo.GetByIdWithParentAsync(cmd.ParentId.Value, ct);
            if (parent is null)
                return Result<ArticleResponse>.Failure(Error.Validation("ParentId", "Página pai não encontrada."));

            // Profundidade máxima: 3 níveis (raiz = nível 1, ... nível 3)
            var parentDepth = await repo.GetDepthAsync(parent.Id, ct);
            if (parentDepth >= 3)
                return Result<ArticleResponse>.Failure(Error.Validation("ParentId", "Profundidade máxima de 3 níveis de páginas atingida."));

            // Subpágina herda escopo e status da raiz
            var root = await repo.GetRootAsync(parent.Id, ct);
            if (root is not null)
            {
                clientId = root.ClientId;
                siteId = root.SiteId;
                departmentId = root.DepartmentId;
                status = root.Status;
            }
        }

        var article = new KnowledgeArticle
        {
            Title = cmd.Title,
            Content = cmd.Content,
            Category = cmd.Category,
            TagsJson = cmd.Tags is { Count: > 0 } ? JsonSerializer.Serialize(cmd.Tags) : null,
            CreatedBy = cmd.CreatedBy,
            LastEditedBy = cmd.CreatedBy,
            ClientId = clientId,
            SiteId = siteId,
            DepartmentId = departmentId,
            ParentId = cmd.ParentId,
            SortOrder = cmd.SortOrder,
            IsPage = cmd.IsPage,
            Status = status
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
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
        ParentId: a.ParentId, SortOrder: a.SortOrder, IsPage: a.IsPage);

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
        var article = await repo.GetByIdWithParentAsync(cmd.Id, ct);
        if (article is null) return Result<ArticleResponse>.Failure(Error.NotFound($"Article {cmd.Id} not found"));

        // Impede ciclo: não pode ser pai de si mesmo
        if (cmd.ParentId.HasValue && cmd.ParentId.Value == cmd.Id)
            return Result<ArticleResponse>.Failure(Error.Validation("ParentId", "Uma página não pode ser pai de si mesma."));

        // Se mudou de pai, valida profundidade, ciclo indireto e herança
        if (cmd.ParentId != article.ParentId)
        {
            if (cmd.ParentId.HasValue)
            {
                var parent = await repo.GetByIdWithParentAsync(cmd.ParentId.Value, ct);
                if (parent is null)
                    return Result<ArticleResponse>.Failure(Error.Validation("ParentId", "Página pai não encontrada."));

                // Impede ciclo indireto: a página não pode ser movida para baixo de um de seus próprios descendentes
                if (await repo.IsDescendantAsync(cmd.Id, cmd.ParentId.Value, ct))
                    return Result<ArticleResponse>.Failure(Error.Validation("ParentId", "Não é possível mover uma página para dentro de sua própria subárvore (criaria um ciclo)."));

                // Profundidade máxima: 3 níveis. Considera a subárvore inteira da página sendo movida.
                var parentDepth = await repo.GetDepthAsync(parent.Id, ct);
                var subtreeMaxLevel = await repo.GetSubtreeMaxLevelAsync(cmd.Id, ct);
                if (parentDepth + subtreeMaxLevel > 3)
                    return Result<ArticleResponse>.Failure(Error.Validation("ParentId", "Profundidade máxima de 3 níveis de páginas atingida."));

                // Herda escopo e status da nova raiz e propaga para toda a subárvore
                var root = await repo.GetRootAsync(parent.Id, ct);
                if (root is not null)
                {
                    article.ClientId = root.ClientId;
                    article.SiteId = root.SiteId;
                    article.DepartmentId = root.DepartmentId;
                    article.Status = root.Status;
                    await repo.PropagateScopeAndStatusAsync(cmd.Id, root.ClientId, root.SiteId, root.DepartmentId, root.Status, ct);
                }
            }
            else
            {
                // Movendo para raiz: mantém escopo atual, mas propaga o status atual para a subárvore
                await repo.PropagateScopeAndStatusAsync(cmd.Id, article.ClientId, article.SiteId, article.DepartmentId, article.Status, ct);
            }
        }

        article.Title = cmd.Title;
        article.Content = cmd.Content;
        article.Category = cmd.Category;
        article.TagsJson = cmd.Tags is { Count: > 0 } ? JsonSerializer.Serialize(cmd.Tags) : null;
        article.LastEditedBy = cmd.LastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;
        article.ParentId = cmd.ParentId;
        article.SortOrder = cmd.SortOrder;
        // Auto-set de IsPage: uma página com subpáginas é sempre um container
        var hasChildren = await repo.GetSubtreeMaxLevelAsync(cmd.Id, ct) > 1;
        article.IsPage = cmd.IsPage || hasChildren;

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
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt,
        ParentId: a.ParentId, SortOrder: a.SortOrder, IsPage: a.IsPage);

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

        // Propaga o status para toda a subárvore (subpáginas herdam o status da raiz)
        await repo.PropagateScopeAndStatusAsync(cmd.Id, updated.ClientId, updated.SiteId, updated.DepartmentId, updated.Status, ct);

        return Result<ArticleResponse>.Success(new ArticleResponse(
            Id: updated.Id, Title: updated.Title, Content: updated.Content, Category: updated.Category,
            Tags: [], CreatedBy: updated.CreatedBy, LastEditedBy: updated.LastEditedBy,
            LastEditedAt: updated.LastEditedAt, Status: updated.Status,
            Scope: "Global", ScopeOrigin: "global",
            ClientId: updated.ClientId, SiteId: updated.SiteId, ClientName: null, SiteName: null,
            DepartmentId: updated.DepartmentId, CurrentVersionNumber: updated.CurrentVersionNumber,
            PublishedAt: updated.PublishedAt, ChunkCount: 0, EmbeddingsReady: false,
            CreatedAt: updated.CreatedAt, UpdatedAt: updated.UpdatedAt,
            ParentId: updated.ParentId, SortOrder: updated.SortOrder, IsPage: updated.IsPage));
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
