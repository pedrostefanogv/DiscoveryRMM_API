using System.Text.Json;
using Discovery.Core.Entities;
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
    /// Herança de escopo: site → client → global
    /// Se siteId informado: inclui artigos do site + do client (sem site) + globais
    /// Se só clientId: inclui artigos do client + globais
    /// Se nenhum: só globais
    /// </summary>
    public async Task<List<KnowledgeArticle>> ListByScopeAsync(
        Guid? clientId,
        Guid? siteId,
        bool publishedOnly = true,
        string? category = null,
        CancellationToken ct = default)
    {
        var query = db.KnowledgeArticles
            .Include(a => a.Chunks)
            .Where(a => a.DeletedAt == null);

        if (publishedOnly)
            query = query.Where(a => a.IsPublished);

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

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.Category != null && a.Category.ToLower() == category.ToLower());

        return await query.OrderBy(a => a.Title).ToListAsync(ct);
    }

    public async Task<List<KnowledgeArticle>> SearchKeywordAsync(
        string queryText,
        Guid? clientId,
        Guid? siteId,
        CancellationToken ct = default)
    {
        var sanitized = queryText.Replace("%", "").Replace("_", "").Trim();
        var pattern = $"%{sanitized}%";

        var query = db.KnowledgeArticles
            .Where(a => a.DeletedAt == null && a.IsPublished)
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

        return await query.OrderBy(a => a.Title).Take(20).ToListAsync(ct);
    }

    public async Task<List<KnowledgeArticle>> GetArticlesNeedingChunkingAsync(
        int limit = 20,
        CancellationToken ct = default)
        => await db.KnowledgeArticles
            .Where(a => a.DeletedAt == null && a.IsPublished &&
                        (a.LastChunkedAt == null || a.LastChunkedAt < a.UpdatedAt))
            .OrderBy(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

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
}
