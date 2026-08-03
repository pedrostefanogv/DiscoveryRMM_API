using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.Cqrs.Knowledge.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Knowledge;

public sealed class GetArticlePagesQueryHandler(
    IKnowledgeArticlePageRepository repo,
    IKnowledgeArticleRepository articleRepo)
    : IRequestHandler<GetArticlePagesQuery, Result<IReadOnlyList<ArticlePageTreeNode>>>
{
    public async Task<Result<IReadOnlyList<ArticlePageTreeNode>>> Handle(GetArticlePagesQuery q, CancellationToken ct)
    {
        // Valida que o artigo existe
        var article = await articleRepo.GetByIdAsync(q.ArticleId, ct);
        if (article is null)
            return Result<IReadOnlyList<ArticlePageTreeNode>>.Failure(Error.NotFound($"Article {q.ArticleId} not found"));

        var pages = await repo.ListByArticleAsync(q.ArticleId, ct);
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

public sealed class GetArticlePageQueryHandler(IKnowledgeArticlePageRepository repo)
    : IRequestHandler<GetArticlePageQuery, Result<ArticlePageResponse>>
{
    public async Task<Result<ArticlePageResponse>> Handle(GetArticlePageQuery q, CancellationToken ct)
    {
        var page = await repo.GetByIdAsync(q.ArticleId, q.PageId, ct);
        if (page is null) return Result<ArticlePageResponse>.Failure(Error.NotFound($"Page {q.PageId} not found in article {q.ArticleId}"));
        return Result<ArticlePageResponse>.Success(MapToResponse(page));
    }

    private static ArticlePageResponse MapToResponse(KnowledgeArticlePage p) => new(
        Id: p.Id,
        ArticleId: p.ArticleId,
        ParentPageId: p.ParentPageId,
        Title: p.Title,
        Content: p.Content,
        SortOrder: p.SortOrder,
        ChildCount: 0,
        Children: [],
        CreatedAt: p.CreatedAt,
        UpdatedAt: p.UpdatedAt);
}

public sealed class CreateArticlePageCommandHandler(
    IKnowledgeArticlePageRepository repo,
    IKnowledgeArticleRepository articleRepo)
    : IRequestHandler<CreateArticlePageCommand, Result<ArticlePageResponse>>
{
    public async Task<Result<ArticlePageResponse>> Handle(CreateArticlePageCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Title))
            return Result<ArticlePageResponse>.Failure(Error.Validation("Title", "O título da página é obrigatório."));

        // Valida que o artigo existe
        var article = await articleRepo.GetByIdAsync(cmd.ArticleId, ct);
        if (article is null)
            return Result<ArticlePageResponse>.Failure(Error.NotFound($"Article {cmd.ArticleId} not found"));

        if (cmd.ParentPageId.HasValue)
        {
            var parent = await repo.GetByIdAsync(cmd.ArticleId, cmd.ParentPageId.Value, ct);
            if (parent is null)
                return Result<ArticlePageResponse>.Failure(Error.Validation("ParentPageId", "Sub-página pai não encontrada neste artigo."));

            // Profundidade máxima: 3 níveis
            var parentDepth = await repo.GetDepthAsync(parent.Id, ct);
            if (parentDepth >= 3)
                return Result<ArticlePageResponse>.Failure(Error.Validation("ParentPageId", "Profundidade máxima de 3 níveis de sub-páginas atingida."));
        }

        var page = new KnowledgeArticlePage
        {
            ArticleId = cmd.ArticleId,
            ParentPageId = cmd.ParentPageId,
            Title = cmd.Title,
            Content = cmd.Content,
            SortOrder = cmd.SortOrder
        };

        var created = await repo.CreateAsync(page, ct);
        return Result<ArticlePageResponse>.Success(MapToResponse(created));
    }

    private static ArticlePageResponse MapToResponse(KnowledgeArticlePage p) => new(
        Id: p.Id,
        ArticleId: p.ArticleId,
        ParentPageId: p.ParentPageId,
        Title: p.Title,
        Content: p.Content,
        SortOrder: p.SortOrder,
        ChildCount: 0,
        Children: [],
        CreatedAt: p.CreatedAt,
        UpdatedAt: p.UpdatedAt);
}

public sealed class UpdateArticlePageCommandHandler(IKnowledgeArticlePageRepository repo)
    : IRequestHandler<UpdateArticlePageCommand, Result<ArticlePageResponse>>
{
    public async Task<Result<ArticlePageResponse>> Handle(UpdateArticlePageCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Title))
            return Result<ArticlePageResponse>.Failure(Error.Validation("Title", "O título da página é obrigatório."));

        var page = await repo.GetByIdWithParentAsync(cmd.ArticleId, cmd.PageId, ct);
        if (page is null) return Result<ArticlePageResponse>.Failure(Error.NotFound($"Page {cmd.PageId} not found in article {cmd.ArticleId}"));

        // Impede ciclo: não pode ser pai de si mesma
        if (cmd.ParentPageId.HasValue && cmd.ParentPageId.Value == cmd.PageId)
            return Result<ArticlePageResponse>.Failure(Error.Validation("ParentPageId", "Uma sub-página não pode ser pai de si mesma."));

        // Se mudou de pai, valida profundidade e ciclo indireto
        if (cmd.ParentPageId != page.ParentPageId)
        {
            if (cmd.ParentPageId.HasValue)
            {
                var parent = await repo.GetByIdAsync(cmd.ArticleId, cmd.ParentPageId.Value, ct);
                if (parent is null)
                    return Result<ArticlePageResponse>.Failure(Error.Validation("ParentPageId", "Sub-página pai não encontrada neste artigo."));

                if (await repo.IsDescendantAsync(cmd.PageId, cmd.ParentPageId.Value, ct))
                    return Result<ArticlePageResponse>.Failure(Error.Validation("ParentPageId", "Não é possível mover uma sub-página para dentro de sua própria subárvore (criaria um ciclo)."));

                var parentDepth = await repo.GetDepthAsync(parent.Id, ct);
                var subtreeMaxLevel = await repo.GetSubtreeMaxLevelAsync(cmd.PageId, ct);
                if (parentDepth + subtreeMaxLevel > 3)
                    return Result<ArticlePageResponse>.Failure(Error.Validation("ParentPageId", "Profundidade máxima de 3 níveis de sub-páginas atingida."));
            }
        }

        page.Title = cmd.Title;
        page.Content = cmd.Content;
        page.ParentPageId = cmd.ParentPageId;
        page.SortOrder = cmd.SortOrder;

        var updated = await repo.UpdateAsync(page, ct);
        return Result<ArticlePageResponse>.Success(MapToResponse(updated));
    }

    private static ArticlePageResponse MapToResponse(KnowledgeArticlePage p) => new(
        Id: p.Id,
        ArticleId: p.ArticleId,
        ParentPageId: p.ParentPageId,
        Title: p.Title,
        Content: p.Content,
        SortOrder: p.SortOrder,
        ChildCount: 0,
        Children: [],
        CreatedAt: p.CreatedAt,
        UpdatedAt: p.UpdatedAt);
}

public sealed class DeleteArticlePageCommandHandler(IKnowledgeArticlePageRepository repo)
    : IRequestHandler<DeleteArticlePageCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteArticlePageCommand cmd, CancellationToken ct)
    {
        var page = await repo.GetByIdAsync(cmd.ArticleId, cmd.PageId, ct);
        if (page is null) return Result<VoidResult>.Failure(Error.NotFound($"Page {cmd.PageId} not found in article {cmd.ArticleId}"));

        await repo.DeleteAsync(cmd.ArticleId, cmd.PageId, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
