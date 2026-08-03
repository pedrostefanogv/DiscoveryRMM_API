using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class KnowledgeArticlePageRepository(DiscoveryDbContext db) : IKnowledgeArticlePageRepository
{
    public async Task<KnowledgeArticlePage?> GetByIdAsync(Guid articleId, Guid pageId, CancellationToken ct = default)
        => await db.KnowledgeArticlePages
            .FirstOrDefaultAsync(p => p.Id == pageId && p.ArticleId == articleId, ct);

    public async Task<KnowledgeArticlePage?> GetByIdWithParentAsync(Guid articleId, Guid pageId, CancellationToken ct = default)
        => await db.KnowledgeArticlePages
            .Include(p => p.ParentPage)
            .FirstOrDefaultAsync(p => p.Id == pageId && p.ArticleId == articleId, ct);

    public async Task<List<KnowledgeArticlePage>> ListByArticleAsync(Guid articleId, CancellationToken ct = default)
        => await db.KnowledgeArticlePages
            .AsNoTracking()
            .Where(p => p.ArticleId == articleId)
            .OrderBy(p => p.ParentPageId)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync(ct);

    public async Task<KnowledgeArticlePage> CreateAsync(KnowledgeArticlePage page, CancellationToken ct = default)
    {
        page.Id = IdGenerator.NewId();
        page.CreatedAt = DateTime.UtcNow;
        page.UpdatedAt = DateTime.UtcNow;
        db.KnowledgeArticlePages.Add(page);
        await db.SaveChangesAsync(ct);
        return page;
    }

    public async Task<KnowledgeArticlePage> UpdateAsync(KnowledgeArticlePage page, CancellationToken ct = default)
    {
        page.UpdatedAt = DateTime.UtcNow;
        db.KnowledgeArticlePages.Update(page);
        await db.SaveChangesAsync(ct);
        return page;
    }

    public async Task DeleteAsync(Guid articleId, Guid pageId, CancellationToken ct = default)
    {
        // Carrega toda a subárvore e exclui em cascata manualmente (FK Restrict).
        var all = await db.KnowledgeArticlePages
            .Where(p => p.ArticleId == articleId)
            .ToListAsync(ct);

        var childrenMap = new Dictionary<Guid, List<KnowledgeArticlePage>>();
        foreach (var p in all)
        {
            if (p.ParentPageId.HasValue)
            {
                if (!childrenMap.TryGetValue(p.ParentPageId.Value, out var list))
                {
                    list = new List<KnowledgeArticlePage>();
                    childrenMap[p.ParentPageId.Value] = list;
                }
                list.Add(p);
            }
        }

        var toDelete = new List<KnowledgeArticlePage>();
        void Collect(Guid nodeId, HashSet<Guid> visited)
        {
            if (!visited.Add(nodeId)) return;
            var node = all.FirstOrDefault(p => p.Id == nodeId);
            if (node is null) return;
            toDelete.Add(node);
            if (childrenMap.TryGetValue(nodeId, out var children))
            {
                foreach (var child in children)
                    Collect(child.Id, visited);
            }
        }

        Collect(pageId, new HashSet<Guid>());

        if (toDelete.Count > 0)
        {
            db.KnowledgeArticlePages.RemoveRange(toDelete);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> GetDepthAsync(Guid? parentPageId, CancellationToken ct = default)
    {
        if (!parentPageId.HasValue) return 0;

        var depth = 0;
        var currentId = parentPageId.Value;
        var guard = 0;
        while (guard < 10)
        {
            var node = await db.KnowledgeArticlePages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == currentId, ct);
            if (node is null) break;
            depth++;
            if (!node.ParentPageId.HasValue) break;
            currentId = node.ParentPageId.Value;
            guard++;
        }
        return depth;
    }

    public async Task<int> GetSubtreeMaxLevelAsync(Guid pageId, CancellationToken ct = default)
    {
        var all = await db.KnowledgeArticlePages
            .AsNoTracking()
            .Select(p => new { p.Id, p.ParentPageId })
            .ToListAsync(ct);

        var childrenMap = new Dictionary<Guid, List<Guid>>();
        foreach (var p in all)
        {
            if (p.ParentPageId.HasValue)
            {
                if (!childrenMap.TryGetValue(p.ParentPageId.Value, out var list))
                {
                    list = new List<Guid>();
                    childrenMap[p.ParentPageId.Value] = list;
                }
                list.Add(p.Id);
            }
        }

        int MaxLevel(Guid nodeId, int depth, HashSet<Guid> visited)
        {
            if (!visited.Add(nodeId)) return depth;
            if (!childrenMap.TryGetValue(nodeId, out var children) || children.Count == 0)
                return depth;
            var maxChild = depth;
            foreach (var child in children)
                maxChild = Math.Max(maxChild, MaxLevel(child, depth + 1, visited));
            return maxChild;
        }

        return MaxLevel(pageId, 1, new HashSet<Guid>());
    }

    public async Task<bool> IsDescendantAsync(Guid ancestorId, Guid nodeId, CancellationToken ct = default)
    {
        var currentId = nodeId;
        var guard = 0;
        while (guard < 10)
        {
            var node = await db.KnowledgeArticlePages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == currentId, ct);
            if (node is null) return false;
            if (node.ParentPageId == ancestorId) return true;
            if (!node.ParentPageId.HasValue) return false;
            currentId = node.ParentPageId.Value;
            guard++;
        }
        return false;
    }
}
