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

    public async Task<KnowledgeArticle?> GetWithoutChunksAsync(Guid id, CancellationToken ct = default)
        => await db.KnowledgeArticles
            .AsNoTracking()
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

    public Task<List<KnowledgeArticle>> SearchKeywordAsync(
        string queryText,
        Guid? clientId,
        Guid? siteId,
        Guid? departmentId = null,
        bool publishedOnly = false,
        CancellationToken ct = default,
        bool forAi = false)
        => SearchKeywordAsync(queryText, clientId, siteId, departmentId, publishedOnly, ct, forAi, excludeArticleIds: null);

    public async Task<List<KnowledgeArticle>> SearchKeywordAsync(
        string queryText,
        Guid? clientId,
        Guid? siteId,
        Guid? departmentId,
        bool publishedOnly,
        CancellationToken ct,
        bool forAi,
        IReadOnlyCollection<Guid>? excludeArticleIds)
    {
        var terms = KnowledgeKeywordQuery.BuildTerms(queryText);
        if (terms.Count == 0)
            return [];

        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && (a.Status == ArticleStatus.Published.ToString()
                    || a.Status == ArticleStatus.Internal.ToString()
                    || a.Status == ArticleStatus.AiOnly.ToString()));

        if (forAi)
            query = query.Where(a => a.AiIndexingEnabled);

        if (excludeArticleIds is { Count: > 0 })
            query = query.Where(a => !excludeArticleIds.Contains(a.Id));

        query = ApplyKeywordTerms(query, terms);

        // Filtro de escopo (paridade com HasAnyChunkAsync)
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
                a.SiteId == siteId ||
                (a.ClientId == null && a.SiteId == null)),

            _ => query.Where(a => a.ClientId == null && a.SiteId == null)
        };

        query = ApplyStatusVisibility(query, departmentId, publishedOnly, forAi);

        var candidates = await query.Take(40).ToListAsync(ct);

        return candidates
            .OrderByDescending(a => ScoreKeywordMatch(a, terms))
            .ThenBy(a => a.Title)
            .Take(20)
            .ToList();
    }

    private static int ScoreKeywordMatch(KnowledgeArticle article, IReadOnlyList<string> terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (article.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 3;
            if (!string.IsNullOrEmpty(article.Category) && article.Category.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 2;
            if (article.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1;
        }
        return score;
    }

    public async Task<List<KnowledgeArticle>> GetArticlesNeedingChunkingAsync(
        int limit = 20,
        CancellationToken ct = default)
        => await db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && a.AiIndexingEnabled
                && (a.Status == ArticleStatus.Published.ToString()
                    || a.Status == ArticleStatus.Internal.ToString()
                    || a.Status == ArticleStatus.AiOnly.ToString())
                && (a.LastChunkedAt == null || a.LastChunkedAt < a.UpdatedAt))
            .OrderBy(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<bool> HasPublishedArticlesAsync(
        Guid? clientId, Guid? siteId, CancellationToken ct = default, bool forAi = false)
        => await CountPublishedAsync(clientId, siteId, ct, forAi) > 0;

    public async Task<int> CountPublishedAsync(
        Guid? clientId, Guid? siteId, CancellationToken ct = default, bool forAi = false)
    {
        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null);

        query = forAi
            ? query.Where(a => a.AiIndexingEnabled && (a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.AiOnly.ToString()))
            : query.Where(a => a.Status == ArticleStatus.Published.ToString());

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

        // Filtro de departamento: quando selecionado, filtra pelos artigos do departamento;
        // sem departamento selecionado, usuários sem acesso global não veem artigos Internal.
        if (departmentId.HasValue)
        {
            query = query.Where(a => a.DepartmentId == departmentId.Value);
        }
        else if (!hasGlobalAccess)
        {
            query = query.Where(a => a.Status != ArticleStatus.Internal.ToString());
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.Category != null && a.Category.ToLower() == category.ToLower());

        // Ordenação suportada: "title" (padrão legacy), "updatedAt", "createdBy", "status", "scope".
        var sort = string.IsNullOrWhiteSpace(sortBy) ? "title" : sortBy.Trim().ToLowerInvariant();
        var direction = string.IsNullOrWhiteSpace(sortDirection) ? "" : sortDirection.Trim().ToLowerInvariant();

        if (sort is "createdby" or "created_by" or "author" or "status" or "scope")
        {
            var descending = direction == "desc";
            var offset = 0;
            if (!string.IsNullOrWhiteSpace(cursor) &&
                cursor.StartsWith("offset:", StringComparison.Ordinal) &&
                int.TryParse(cursor[7..], out var parsedOffset))
            {
                offset = Math.Max(0, parsedOffset);
            }

            IOrderedQueryable<KnowledgeArticle> ordered = sort switch
            {
                "createdby" or "created_by" or "author" => descending
                    ? query.OrderByDescending(a => a.CreatedBy ?? string.Empty).ThenByDescending(a => a.Id)
                    : query.OrderBy(a => a.CreatedBy ?? string.Empty).ThenBy(a => a.Id),
                "status" => descending
                    ? query.OrderByDescending(a => a.Status).ThenByDescending(a => a.Id)
                    : query.OrderBy(a => a.Status).ThenBy(a => a.Id),
                _ => descending
                    ? query.OrderByDescending(a => a.SiteId != null ? 2 : a.ClientId != null ? 1 : 0).ThenByDescending(a => a.Id)
                    : query.OrderBy(a => a.SiteId != null ? 2 : a.ClientId != null ? 1 : 0).ThenBy(a => a.Id),
            };

            var sortPage = await ordered.Skip(offset).Take(limit + 1).ToListAsync(ct);
            var sortHasMore = sortPage.Count > limit;
            var sortItems = sortHasMore ? sortPage.Take(limit).ToList() : sortPage;
            var sortNextCursor = sortHasMore ? $"offset:{offset + sortItems.Count}" : null;

            return await BuildPageAsync(sortItems, sortHasMore, sortNextCursor, ct);
        }

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
            .Select(g => new
            {
                g.Key,
                Total = g.Count(),
                Embedded = g.Count(c => c.EmbeddingGeneratedAt != null)
            })
            .ToListAsync(ct);

        return new ArticleListPageData
        {
            Items = items,
            Count = items.Count,
            NextCursor = nextCursor,
            HasMore = hasMore,
            ChunkCounts = counts.ToDictionary(x => x.Key, x => x.Total),
            EmbeddedChunkCounts = counts.ToDictionary(x => x.Key, x => x.Embedded)
        };
    }

    public async Task<IReadOnlyList<string>> ListDistinctCategoriesByUserScopeAsync(
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        CancellationToken ct = default)
    {
        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null && a.Category != null && a.Category != "");

        query = ApplyMultiScopeFilter(query, hasGlobalAccess, allowedClientIds, allowedSiteIds);

        var raw = await query
            .Select(a => a.Category!)
            .Distinct()
            .ToListAsync(ct);

        return raw
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<KnowledgeArticle>> SearchKeywordByUserScopeAsync(
        string queryText,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        Guid? departmentId = null,
        bool publishedOnly = false,
        CancellationToken ct = default,
        bool forAi = false)
    {
        var terms = KnowledgeKeywordQuery.BuildTerms(queryText);
        if (terms.Count == 0)
            return [];

        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && (a.Status == ArticleStatus.Published.ToString()
                    || a.Status == ArticleStatus.Internal.ToString()
                    || a.Status == ArticleStatus.AiOnly.ToString()));

        if (forAi)
            query = query.Where(a => a.AiIndexingEnabled);

        query = ApplyKeywordTerms(query, terms);
        query = ApplyMultiScopeFilter(query, hasGlobalAccess, allowedClientIds, allowedSiteIds);

        if (publishedOnly)
        {
            query = forAi
                ? query.Where(a => a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.AiOnly.ToString())
                : query.Where(a => a.Status == ArticleStatus.Published.ToString());
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
        bool publishedOnly,
        bool forAi = false)
    {
        if (publishedOnly)
        {
            return forAi
                ? query.Where(a => a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.AiOnly.ToString())
                : query.Where(a => a.Status == ArticleStatus.Published.ToString());
        }

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
