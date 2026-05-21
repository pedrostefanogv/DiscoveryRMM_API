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

    /// <summary>
    /// Herança de escopo: site → client → global.
    /// Filtro de status: null = retorna todos visíveis ao usuário atual
    /// (Published/Internal no mesmo departamento, mais Drafts do próprio usuário se aplicável)
    /// </summary>
    public async Task<List<KnowledgeArticle>> ListByScopeAsync(
        Guid? clientId,
        Guid? siteId,
        string? status = null,
        Guid? departmentId = null,
        string? category = null,
        CancellationToken ct = default)
    {
        var query = db.KnowledgeArticles
            .Include(a => a.Chunks)
            .Where(a => a.DeletedAt == null);

        // Filtro de status
        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(a => a.Status == status);
        }

        // Filtro de herança de escopo
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

        // Filtro de departamento (para artigos Internal)
        if (departmentId.HasValue)
        {
            query = query.Where(a =>
                a.Status != ArticleStatus.Internal.ToString() ||
                a.DepartmentId == departmentId.Value);
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.Category != null && a.Category.ToLower() == category.ToLower());

        return await query.OrderBy(a => a.Title).ToListAsync(ct);
    }

    public async Task<List<KnowledgeArticle>> SearchKeywordAsync(
        string queryText,
        Guid? clientId,
        Guid? siteId,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var sanitized = queryText.Replace("%", "").Replace("_", "").Trim();
        var pattern = $"%{sanitized}%";

        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && (a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.Internal.ToString()))
            .Where(a =>
                EF.Functions.ILike(a.Title, pattern) ||
                EF.Functions.ILike(a.Content, pattern) ||
                (a.Category != null && EF.Functions.ILike(a.Category, pattern)));

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

        // Filtro de departamento para artigos Internal
        if (departmentId.HasValue)
        {
            query = query.Where(a =>
                a.Status != ArticleStatus.Internal.ToString() ||
                a.DepartmentId == departmentId.Value);
        }

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
        CancellationToken ct = default)
    {
        var query = db.KnowledgeArticles
            .Include(a => a.Chunks)
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

        // Filtro multi-escopo via ACL
        query = ApplyMultiScopeFilter(query, hasGlobalAccess, allowedClientIds, allowedSiteIds);

        // Filtro de departamento (para artigos Internal)
        if (departmentId.HasValue)
        {
            query = query.Where(a =>
                a.Status != ArticleStatus.Internal.ToString() ||
                a.DepartmentId == departmentId.Value);
        }
        else
        {
            // Sem departmentId, Internal não aparece
            query = query.Where(a => a.Status != ArticleStatus.Internal.ToString());
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.Category != null && a.Category.ToLower() == category.ToLower());

        // Paginação cursor-based: cursor = base64(cursor_value)
        // Usamos Title + Id como chave composta (ordena por Title, desempata por Id)
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorValue = DecodePaginationCursor(cursor);
            if (cursorValue is not null)
            {
                query = query.Where(a =>
                    string.Compare(a.Title, cursorValue.Title) > 0 ||
                    (a.Title == cursorValue.Title && a.Id.CompareTo(cursorValue.Id) > 0));
            }
        }

        var orderedQuery = query.OrderBy(a => a.Title).ThenBy(a => a.Id);
        var page = await orderedQuery.Take(limit + 1).ToListAsync(ct);

        var hasMore = page.Count > limit;
        var items = hasMore ? page.Take(limit).ToList() : page;

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = EncodePaginationCursor(last.Title, last.Id);
        }

        return new ArticleListPageData
        {
            Items = items,
            Count = items.Count,
            NextCursor = nextCursor,
            HasMore = hasMore
        };
    }

    public async Task<List<KnowledgeArticle>> SearchKeywordByUserScopeAsync(
        string queryText,
        bool hasGlobalAccess,
        IReadOnlySet<Guid> allowedClientIds,
        IReadOnlySet<Guid> allowedSiteIds,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var sanitized = queryText.Replace("%", "").Replace("_", "").Trim();
        var pattern = $"%{sanitized}%";

        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null
                && (a.Status == ArticleStatus.Published.ToString() || a.Status == ArticleStatus.Internal.ToString()))
            .Where(a =>
                EF.Functions.ILike(a.Title, pattern) ||
                EF.Functions.ILike(a.Content, pattern) ||
                (a.Category != null && EF.Functions.ILike(a.Category, pattern)));

        query = ApplyMultiScopeFilter(query, hasGlobalAccess, allowedClientIds, allowedSiteIds);

        if (departmentId.HasValue)
        {
            query = query.Where(a =>
                a.Status != ArticleStatus.Internal.ToString() ||
                a.DepartmentId == departmentId.Value);
        }
        else
        {
            query = query.Where(a => a.Status != ArticleStatus.Internal.ToString());
        }

        return await query.OrderBy(a => a.Title).Take(20).ToListAsync(ct);
    }

    // ─── Helpers de cursor ─────────────────────────────────────

    private static string EncodePaginationCursor(string title, Guid id)
    {
        var combined = $"{title}|||{id:N}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        return Convert.ToBase64String(bytes);
    }

    private static (string Title, Guid Id)? DecodePaginationCursor(string cursor)
    {
        try
        {
            var bytes = Convert.FromBase64String(cursor);
            var combined = System.Text.Encoding.UTF8.GetString(bytes);
            var parts = combined.Split("|||");
            if (parts.Length == 2 && Guid.TryParse(parts[1], out var id))
                return (parts[0], id);
            return null;
        }
        catch
        {
            return null;
        }
    }
}
