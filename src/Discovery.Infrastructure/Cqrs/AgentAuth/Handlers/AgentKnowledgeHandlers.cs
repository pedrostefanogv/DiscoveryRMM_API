using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Knowledge;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetKnowledgeArticlesHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IKnowledgeArticleRepository knowledgeRepo
) : IRequestHandler<GetKnowledgeArticlesQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetKnowledgeArticlesQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);

        // Usa o método unificado (ACL-based) com filtro de escopo.
        // Agents têm escopo fixo (site → client → global) — passamos filterClientId/filterSiteId
        // que fazem herança de escopo automaticamente.
        var data = await knowledgeRepo.ListByUserScopeAsync(
            hasGlobalAccess: false,
            allowedClientIds: new HashSet<Guid>(),
            allowedSiteIds: new HashSet<Guid>(),
            status: "Published",
            departmentId: null,
            category: q.Category,
            cursor: null,
            limit: 500,
            filterClientId: site?.ClientId,
            filterSiteId: agent.SiteId,
            ct: ct);

        // Mapeia para DTO plano (sem navigation properties → sem ciclo de serialização)
        var dtos = data.Items.Select(MapToDto).ToList();
        return Result<object>.Success(dtos);
    }

    private static AgentKnowledgeArticleDto MapToDto(KnowledgeArticle a) => new(
        Id: a.Id,
        Title: a.Title,
        Content: a.Content,
        Category: a.Category,
        Tags: ParseTags(a.TagsJson),
        TagsJson: a.TagsJson,
        Status: a.Status,
        CreatedBy: a.CreatedBy,
        LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt,
        ClientId: a.ClientId,
        SiteId: a.SiteId,
        DepartmentId: a.DepartmentId,
        CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt,
        CreatedAt: a.CreatedAt,
        UpdatedAt: a.UpdatedAt);

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}

public sealed class GetKnowledgeArticleHandler(
    IKnowledgeArticleRepository knowledgeRepo
) : IRequestHandler<GetKnowledgeArticleQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetKnowledgeArticleQuery q, CancellationToken ct)
    {
        var article = await knowledgeRepo.GetByIdAsync(q.ArticleId, ct);
        if (article is null)
            return Result<object>.Failure(Error.NotFound("Knowledge article not found."));

        // Mapeia para DTO plano para evitar ciclo de serialização
        var dto = new AgentKnowledgeArticleDto(
            Id: article.Id,
            Title: article.Title,
            Content: article.Content,
            Category: article.Category,
            Tags: ParseTags(article.TagsJson),
            TagsJson: article.TagsJson,
            Status: article.Status,
            CreatedBy: article.CreatedBy,
            LastEditedBy: article.LastEditedBy,
            LastEditedAt: article.LastEditedAt,
            ClientId: article.ClientId,
            SiteId: article.SiteId,
            DepartmentId: article.DepartmentId,
            CurrentVersionNumber: article.CurrentVersionNumber,
            PublishedAt: article.PublishedAt,
            CreatedAt: article.CreatedAt,
            UpdatedAt: article.UpdatedAt);

        return Result<object>.Success(dto);
    }

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}

public sealed class GetKnowledgeArticlePagesHandler(
    IKnowledgeArticleRepository knowledgeRepo,
    IKnowledgeArticlePageRepository pageRepo)
    : IRequestHandler<GetKnowledgeArticlePagesQuery, Result<IReadOnlyList<ArticlePageTreeNode>>>
{
    public async Task<Result<IReadOnlyList<ArticlePageTreeNode>>> Handle(GetKnowledgeArticlePagesQuery q, CancellationToken ct)
    {
        // Consistente com GetKnowledgeArticleHandler (detalhe): não valida escopo aqui,
        // pois o agente obtém os artigos da lista já filtrada por escopo (ListByUserScopeAsync).
        var article = await knowledgeRepo.GetByIdAsync(q.ArticleId, ct);
        if (article is null)
            return Result<IReadOnlyList<ArticlePageTreeNode>>.Failure(Error.NotFound("Knowledge article not found."));

        var pages = await pageRepo.ListByArticleAsync(q.ArticleId, ct);
        var roots = BuildTree(pages);
        return Result<IReadOnlyList<ArticlePageTreeNode>>.Success(roots);
    }

    private static IReadOnlyList<ArticlePageTreeNode> BuildTree(List<KnowledgeArticlePage> pages)
    {
        var childrenMap = new Dictionary<Guid, List<KnowledgeArticlePage>>();
        foreach (var p in pages)
        {
            var key = p.ParentPageId ?? Guid.Empty;
            if (!childrenMap.TryGetValue(key, out var list))
            {
                list = new List<KnowledgeArticlePage>();
                childrenMap[key] = list;
            }
            list.Add(p);
        }

        foreach (var list in childrenMap.Values)
        {
            list.Sort((x, y) =>
            {
                var c = x.SortOrder.CompareTo(y.SortOrder);
                return c != 0 ? c : string.Compare(x.Title, y.Title, StringComparison.OrdinalIgnoreCase);
            });
        }

        IReadOnlyList<ArticlePageTreeNode> Build(Guid? parentId, int depth = 0)
        {
            if (depth > 10) return []; // proteção contra ciclos
            var key = parentId ?? Guid.Empty;
            if (!childrenMap.TryGetValue(key, out var siblings))
                return [];

            var nodes = new List<ArticlePageTreeNode>(siblings.Count);
            foreach (var p in siblings)
            {
                var children = Build(p.Id, depth + 1);
                nodes.Add(new ArticlePageTreeNode(
                    Id: p.Id,
                    ArticleId: p.ArticleId,
                    ParentPageId: p.ParentPageId,
                    Title: p.Title,
                    Content: p.Content,
                    SortOrder: p.SortOrder,
                    ChildCount: children.Count,
                    Children: children));
            }
            return nodes;
        }

        return Build(null);
    }
}