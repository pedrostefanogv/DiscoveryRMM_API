using System.Linq.Expressions;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class KnowledgeArticleRepository(DiscoveryDbContext db) : IKnowledgeArticleRepository
{
    public async Task<KnowledgeArticle?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.KnowledgeArticles
            .Include(a => a.Chunks)
            .FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);

    public async Task<KnowledgeArticle> CreateAsync(KnowledgeArticle article, CancellationToken ct = default)
    {
        article.Id = IdGenerator.NewId();
        article.Status = ArticleStatus.Draft.ToString();
        article.CurrentVersionNumber = 0;
        article.CreatedAt = DateTime.UtcNow;
        article.UpdatedAt = DateTime.UtcNow;
        db.KnowledgeArticles.Add(article);
        await db.SaveChangesAsync(ct);
        return article;
    }

    public async Task<KnowledgeArticle> UpdateAsync(KnowledgeArticle article, CancellationToken ct = default)
    {
        article.UpdatedAt = DateTime.UtcNow;
        db.KnowledgeArticles.Update(article);
        await db.SaveChangesAsync(ct);
        return article;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var article = await db.KnowledgeArticles.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (article == null) return;
        article.DeletedAt = DateTime.UtcNow;
        article.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<KnowledgeArticle>> SearchKeywordAsync(
        string queryText,
        Guid? clientId,
        Guid? siteId,
        Guid? departmentId = null,
        bool publishedOnly = false,
        CancellationToken ct = default)
    {
        var terms = KnowledgeKeywordQuery.BuildTerms(queryText);
        if (terms.Count == 0)
            return [];

        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && (a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.Internal.ToString()));

        query = ApplyKeywordTerms(query, terms);

        // Filtro de escopo
        query = (clientId, siteId) switch
        {
            (not null, not null) => query.Where(a =>
                (a.SiteId == siteId) ||
                (a.ClientId == clientId && a.SiteId == null) ||
                (a.ClientId == null && a.SiteId == null)),

            (not null, null) => query.Where(a =>
                (a.ClientId == clientId && a.SiteId == null) ||
                (a.ClientId == null && a.SiteId == null)),

            _ => query.Where(a => a.ClientId == null && a.SiteId == null)
        };

        query = ApplyStatusVisibility(query, departmentId, publishedOnly);

        return await query.OrderBy(a => a.Title).Take(20).ToListAsync(ct);
    }

    public async Task<List<KnowledgeArticle>> GetArticlesNeedingChunkingAsync(
        int limit = 20,
        CancellationToken ct = default)
        => await db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && (a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.Internal.ToString())
                && (a.LastChunkedAt == null || a.LastChunkedAt < a.UpdatedAt))
            .OrderBy(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<bool> HasPublishedArticlesAsync(
        Guid? clientId, Guid? siteId, CancellationToken ct = default)
        => await CountPublishedAsync(clientId, siteId, ct) > 0;

    public async Task<int> CountPublishedAsync(
        Guid? clientId, Guid? siteId, CancellationToken ct = default)
    {
        // Somente artigos PUBLICADOS: é o que o chat pode expor ao usuário.
        // Artigos Internal orientam o departamento e não entram aqui.
        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && a.Status == ArticleStatus.Published.ToString());

        // Herança de escopo: site → client → global
        query = (clientId, siteId) switch
        {
            (not null, not null) => query.Where(a =>
                (a.SiteId == siteId) ||
                (a.ClientId == clientId && a.SiteId == null) ||
                (a.ClientId == null && a.SiteId == null)),
            (not null, null) => query.Where(a =>
                (a.ClientId == clientId && a.SiteId == null) ||
                (a.ClientId == null && a.SiteId == null)),
            (null, not null) => query.Where(a =>
                a.SiteId == siteId || (a.ClientId == null && a.SiteId == null)),
            _ => query.Where(a => a.ClientId == null && a.SiteId == null)
        };

        return await query.CountAsync(ct);
    }

    // ─── Versionamento ──────────────────────────────────────────────

    public async Task<KnowledgeArticleVersion> CreateVersionAsync(
        KnowledgeArticleVersion version, CancellationToken ct = default)
    {
        version.Id = IdGenerator.NewId();
        version.CreatedAt = DateTime.UtcNow;
        db.KnowledgeArticleVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return version;
    }

    public async Task<List<KnowledgeArticleVersion>> GetVersionsAsync(
        Guid articleId, CancellationToken ct = default)
        => await db.KnowledgeArticleVersions
            .Where(v => v.ArticleId == articleId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);

    public async Task<KnowledgeArticleVersion?> GetVersionAsync(
        Guid articleId, int versionNumber, CancellationToken ct = default)
        => await db.KnowledgeArticleVersions
            .FirstOrDefaultAsync(v => v.ArticleId == articleId && v.VersionNumber == versionNumber, ct);

    // ─── Ticket ↔ KB ───────────────────────────────────────────────

    public async Task<List<KnowledgeArticle>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default)
        => await db.TicketKnowledgeLinks
            .Where(l => l.TicketId == ticketId)
            .Include(l => l.Article)
            .Select(l => l.Article)
            .Where(a => a.DeletedAt == null)
            .OrderBy(a => a.Title)
            .ToListAsync(ct);

    public async Task<TicketKnowledgeLink?> GetLinkAsync(Guid ticketId, Guid articleId, CancellationToken ct = default)
        => await db.TicketKnowledgeLinks
            .FirstOrDefaultAsync(l => l.TicketId == ticketId && l.ArticleId == articleId, ct);

    public async Task<TicketKnowledgeLink> LinkToTicketAsync(
        Guid ticketId, Guid articleId, string? linkedBy, string? note, CancellationToken ct = default)
    {
        var link = new TicketKnowledgeLink
        {
            Id = IdGenerator.NewId(),
            TicketId = ticketId,
            ArticleId = articleId,
            LinkedBy = linkedBy,
            Note = note,
            LinkedAt = DateTime.UtcNow
        };
        db.TicketKnowledgeLinks.Add(link);
        await db.SaveChangesAsync(ct);
        return link;
    }

    public async Task UnlinkFromTicketAsync(Guid ticketId, Guid articleId, CancellationToken ct = default)
    {
        var link = await db.TicketKnowledgeLinks
            .FirstOrDefaultAsync(l => l.TicketId == ticketId && l.ArticleId == articleId, ct);
        if (link == null) return;
        db.TicketKnowledgeLinks.Remove(link);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<TicketKnowledgeLink>> GetTicketLinksAsync(Guid ticketId, CancellationToken ct = default)
        => await db.TicketKnowledgeLinks
            .Include(l => l.Article)
            .Where(l => l.TicketId == ticketId)
            .OrderBy(l => l.LinkedAt)
            .ToListAsync(ct);

    public async Task<TicketKnowledgeLink> UpdateLinkAsync(TicketKnowledgeLink link, CancellationToken ct = default)
    {
        db.TicketKnowledgeLinks.Update(link);
        await db.SaveChangesAsync(ct);
        return link;
    }

    // ─── ACL multi-escopo + paginação cursor-based ──────────────

    /// <summary>
    /// Monta a cláusula WHERE de escopo para múltiplos clientes/sites.
    /// </summary>
    private static IQueryable<KnowledgeArticle> ApplyMultiScopeFilter(
        IQueryable<KnowledgeArticle> query,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds)
    {
        if (hasGlobalAccess)
            return query; // vê tudo

        var clientList = allowedClientIds.ToList();
        var siteList = allowedSiteIds.ToList();

        if (clientList.Count == 0 && siteList.Count == 0)
        {
            // Sem acesso a nenhum cliente/site específico → só globais
            return query.Where(a => a.ClientId == null && a.SiteId == null);
        }

        if (clientList.Count > 0 && siteList.Count > 0)
        {
            return query.Where(a =>
                (a.ClientId == null && a.SiteId == null) ||                         // globais
                (a.ClientId != null && a.SiteId == null && clientList.Contains(a.ClientId.Value)) ||  // client-level
                (a.SiteId != null && siteList.Contains(a.SiteId.Value)));            // site-level
        }

        if (clientList.Count > 0)
        {
            return query.Where(a =>
                (a.ClientId == null && a.SiteId == null) ||
                (a.ClientId != null && a.SiteId == null && clientList.Contains(a.ClientId.Value)) ||
                (a.SiteId != null && clientList.Contains(a.ClientId!.Value)));
        }

        // Só sites
        return query.Where(a =>
            (a.ClientId == null && a.SiteId == null) ||
            (a.SiteId != null && siteList.Contains(a.SiteId.Value)));
    }

    public async Task<ArticleListPageData> ListByUserScopeAsync(
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        string? status = null,
        Guid? departmentId = null,
        string? category = null,
        string? cursor = null,
        int limit = 20,
        Guid? filterClientId = null,
        Guid? filterSiteId = null,
        string? sortBy = null,
        string? sortDirection = null,
        CancellationToken ct = default)
    {
        // Perf: sem Include(Chunks) — carregaria Content + Embedding (1536 dims) de
        // todos os chunks só para expor ChunkCount. Os totais vêm de uma query
        // agregada leve sobre a página retornada (ver ChunkCounts / BuildPageAsync).
        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null);

        // Filtro de status
        if (!string.IsNullOrEmpty(status))
        {
            var statusFilter = status;
            if (statusFilter == "visible")
            {
                // Visible = Published + Internal (do departamento)
                // departmentId já é tratado abaixo
                query = query.Where(a =>
                    a.Status == ArticleStatus.Published.ToString() ||
                    a.Status == ArticleStatus.Internal.ToString());
            }
            else
            {
                query = query.Where(a => a.Status == statusFilter);
            }
        }

        // Filtro de escopo: se o usuário selecionou um cliente/site, refina;
        // caso contrário, aplica ACL multi-escopo (vê tudo que pode acessar).
        if (filterClientId.HasValue && filterSiteId.HasValue)
        {
            query = query.Where(a =>
                (a.ClientId == null && a.SiteId == null) ||
                (a.ClientId == filterClientId.Value && a.SiteId == null) ||
                (a.SiteId == filterSiteId.Value));
        }
        else if (filterClientId.HasValue)
        {
            query = query.Where(a =>
                (a.ClientId == null && a.SiteId == null) ||
                (a.ClientId == filterClientId.Value && a.SiteId == null));
        }
        else
        {
            query = ApplyMultiScopeFilter(query, hasGlobalAccess, allowedClientIds, allowedSiteIds);
        }

        // Filtro de departamento (para artigos Internal).
        // Com acesso global (admin), Internal fica visível mesmo sem departmentId;
        // sem acesso global, Internal só aparece quando o departamento confere.
        if (departmentId.HasValue)
        {
            query = query.Where(a =>
                a.Status != ArticleStatus.Internal.ToString() ||
                a.DepartmentId == departmentId.Value);
        }
        else if (!hasGlobalAccess)
        {
            query = query.Where(a => a.Status != ArticleStatus.Internal.ToString());
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.Category != null && a.Category.ToLower() == category.ToLower());

        // Ordenação suportada: "title" (padrão legacy) e "updatedAt".
        // Direção: title default asc (compatível); updatedAt default desc (esperado pela UI).
        var sort = string.IsNullOrWhiteSpace(sortBy) ? "title" : sortBy.Trim().ToLowerInvariant();
        var direction = string.IsNullOrWhiteSpace(sortDirection) ? "" : sortDirection.Trim().ToLowerInvariant();

        if (sort is "updatedat" or "updated_at" or "updated")
        {
            var descending = direction != "asc";

            // Cursor Type A (ticks|guidN) sobre UpdatedAt + Id
            if (!string.IsNullOrWhiteSpace(cursor) &&
                CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorDate, out var cursorId))
            {
                query = descending
                    ? query.Where(a =>
                        a.UpdatedAt < cursorDate ||
                        (a.UpdatedAt == cursorDate && a.Id.CompareTo(cursorId) < 0))
                    : query.Where(a =>
                        a.UpdatedAt > cursorDate ||
                        (a.UpdatedAt == cursorDate && a.Id.CompareTo(cursorId) > 0));
            }

            var ordered = descending
                ? query.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Id)
                : query.OrderBy(a => a.UpdatedAt).ThenBy(a => a.Id);
            var page = await ordered.Take(limit + 1).ToListAsync(ct);

            var hasMore = page.Count > limit;
            var items = hasMore ? page.Take(limit).ToList() : page;

            string? nextCursor = null;
            if (hasMore && items.Count > 0)
            {
                var last = items[^1];
                nextCursor = CursorPaginationHelper.EncodeCreatedAtCursor(last.UpdatedAt, last.Id);
            }

            return await BuildPageAsync(items, hasMore, nextCursor, ct);
        }

        // Paginação cursor-based por Title: cursor = base64(name|guid) (Type C)
        // Chave composta Title + Id; title desc desempata por Id asc.
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (CursorPaginationHelper.TryDecodeNameCursor(cursor, out var cursorName, out var cursorId))
            {
                query = direction == "desc"
                    ? query.Where(a =>
                        string.Compare(a.Title, cursorName) < 0 ||
                        (a.Title == cursorName && a.Id.CompareTo(cursorId) > 0))
                    : query.Where(a =>
                        string.Compare(a.Title, cursorName) > 0 ||
                        (a.Title == cursorName && a.Id.CompareTo(cursorId) > 0));
            }
        }

        var orderedQuery = direction == "desc"
            ? query.OrderByDescending(a => a.Title).ThenBy(a => a.Id)
            : query.OrderBy(a => a.Title).ThenBy(a => a.Id);
        var titlePage = await orderedQuery.Take(limit + 1).ToListAsync(ct);

        var titleHasMore = titlePage.Count > limit;
        var titleItems = titleHasMore ? titlePage.Take(limit).ToList() : titlePage;

        string? titleNextCursor = null;
        if (titleHasMore && titleItems.Count > 0)
        {
            var last = titleItems[^1];
            titleNextCursor = CursorPaginationHelper.EncodeNameCursor(last.Title, last.Id);
        }

        return await BuildPageAsync(titleItems, titleHasMore, titleNextCursor, ct);
    }

    /// <summary>
    /// Monta ArticleListPageData com ChunkCounts agregados (uma query GROUP BY leve
    /// sobre a página retornada, sem materializar chunks/embeddings).
    /// </summary>
    private async Task<ArticleListPageData> BuildPageAsync(
        List<KnowledgeArticle> items, bool hasMore, string? nextCursor, CancellationToken ct)
    {
        var ids = items.Select(a => a.Id).ToList();
        var counts = await db.KnowledgeArticleChunks
            .Where(c => ids.Contains(c.ArticleId))
            .GroupBy(c => c.ArticleId)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToListAsync(ct);

        return new ArticleListPageData
        {
            Items = items,
            Count = items.Count,
            NextCursor = nextCursor,
            HasMore = hasMore,
            ChunkCounts = counts.ToDictionary(x => x.Key, x => x.Total)
        };
    }

    public async Task<List<KnowledgeArticle>> SearchKeywordByUserScopeAsync(
        string queryText,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        Guid? departmentId = null,
        bool publishedOnly = false,
        CancellationToken ct = default)
    {
        var terms = KnowledgeKeywordQuery.BuildTerms(queryText);
        if (terms.Count == 0)
            return [];

        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && (a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.Internal.ToString()));

        query = ApplyKeywordTerms(query, terms);
        query = ApplyMultiScopeFilter(query, hasGlobalAccess, allowedClientIds, allowedSiteIds);

        // "Published only" (chat do agent) tem precedência sobre a visibilidade de
        // Internal. Sem ele, mantém a regra da listagem: com acesso global Internal
        // fica visível; sem acesso global, só quando o departamento confere.
        if (publishedOnly)
        {
            query = query.Where(a => a.Status == ArticleStatus.Published.ToString());
        }
        else if (departmentId.HasValue)
        {
            query = query.Where(a =>
                a.Status != ArticleStatus.Internal.ToString() ||
                a.DepartmentId == departmentId.Value);
        }
        else if (!hasGlobalAccess)
        {
            query = query.Where(a => a.Status != ArticleStatus.Internal.ToString());
        }

        return await query.OrderBy(a => a.Title).Take(20).ToListAsync(ct);
    }

    /// <summary>
    /// Monta o OR de ILIKE para cada termo da busca, casando title + content +
    /// category + tags. Antes a query inteira era um único ILIKE e a coluna de
    /// tags (prometida no contrato da interface) era ignorada.
    /// </summary>
    private static IQueryable<KnowledgeArticle> ApplyKeywordTerms(
        IQueryable<KnowledgeArticle> query,
        IReadOnlyList<string> terms)
    {
        Expression<Func<KnowledgeArticle, bool>>? predicate = null;

        foreach (var term in terms)
        {
            var pattern = $"%{term}%";
            Expression<Func<KnowledgeArticle, bool>> termPredicate = a =>
                EF.Functions.ILike(a.Title, pattern) ||
                EF.Functions.ILike(a.Content, pattern) ||
                (a.Category != null && EF.Functions.ILike(a.Category, pattern)) ||
                (a.TagsJson != null && EF.Functions.ILike(a.TagsJson, pattern));

            predicate = predicate is null ? termPredicate : OrElse(predicate, termPredicate);
        }

        return query.Where(predicate!);
    }

    /// <summary>
    /// Visibilidade de status para o caminho legado (clientId/siteId).
    /// Com <paramref name="publishedOnly"/> retorna só Published; sem departamento
    /// informado exclui Internal (alinhado à busca semântica e ao contrato da interface).
    /// </summary>
    private static IQueryable<KnowledgeArticle> ApplyStatusVisibility(
        IQueryable<KnowledgeArticle> query,
        Guid? departmentId,
        bool publishedOnly)
    {
        if (publishedOnly)
            return query.Where(a => a.Status == ArticleStatus.Published.ToString());

        if (departmentId.HasValue)
            return query.Where(a =>
                a.Status != ArticleStatus.Internal.ToString() ||
                a.DepartmentId == departmentId.Value);

        return query.Where(a => a.Status != ArticleStatus.Internal.ToString());
    }

    private static Expression<Func<T, bool>> OrElse<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T), "a");
        var body = Expression.OrElse(
            new ParameterReplaceVisitor(left.Parameters[0], parameter).Visit(left.Body)!,
            new ParameterReplaceVisitor(right.Parameters[0], parameter).Visit(right.Body)!);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>Rebaseia referências ao parâmetro original para o parâmetro unificado do OR.</summary>
    private sealed class ParameterReplaceVisitor(ParameterExpression from, ParameterExpression to)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }

}
