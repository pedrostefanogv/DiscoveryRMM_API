using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

/// <inheritdoc />
public class TicketAnswerRepository(DiscoveryDbContext db) : ITicketAnswerRepository
{
    public async Task<IReadOnlyList<TicketAnswerEmbeddingJobItem>> GetWithoutEmbeddingAsync(
        int limit,
        CancellationToken ct = default)
    {
        var cap = Math.Clamp(limit, 1, 200);

        var tickets = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Select(t => new { t.Id, t.ClientId, t.SiteId });

        var query =
            from a in db.TicketAnswers.AsNoTracking()
            join t in tickets on a.TicketId equals t.Id
            where a.Embedding == null
                  && a.EmbeddingGeneratedAt == null
                  && !a.IsSensitive
                  && a.ValueText != null
                  && a.ValueText != ""
            orderby a.CreatedAt
            select new TicketAnswerEmbeddingJobItem(
                a.Id, a.TicketId, a.QuestionKey, a.QuestionLabel, a.ValueText!, t.ClientId, t.SiteId);

        return await query.Take(cap).ToListAsync(ct);
    }

    public async Task UpdateEmbeddingsAsync(
        IReadOnlyList<TicketAnswerEmbeddingUpdate> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;

        var ids = updates.Select(u => u.AnswerId).ToList();
        var byId = updates.ToDictionary(u => u.AnswerId);
        var rows = await db.TicketAnswers.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.Id, out var update)) continue;
            row.Embedding = new Vector(update.Embedding);
            row.EmbeddingGeneratedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task MarkProcessedWithoutEmbeddingAsync(
        IReadOnlyList<Guid> answerIds,
        CancellationToken ct = default)
    {
        if (answerIds.Count == 0) return;

        var now = DateTime.UtcNow;
        await db.TicketAnswers
            .Where(a => answerIds.Contains(a.Id) && a.Embedding == null && a.EmbeddingGeneratedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.EmbeddingGeneratedAt, now), ct);
    }

    public async Task<IReadOnlyList<TicketAnswerSearchHit>> SearchKeywordAsync(
        string term,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        IReadOnlyCollection<Guid> allowedSiteIds,
        Guid? templateId,
        string? questionKey,
        int limit,
        CancellationToken ct = default)
    {
        if (!hasGlobalAccess && allowedClientIds.Count == 0 && allowedSiteIds.Count == 0)
            return Array.Empty<TicketAnswerSearchHit>();
        if (string.IsNullOrWhiteSpace(term))
            return Array.Empty<TicketAnswerSearchHit>();

        var cap = Math.Clamp(limit, 1, 50);
        var allowedClients = allowedClientIds.Distinct().ToArray();
        var allowedSites = allowedSiteIds.Distinct().ToArray();
        var pattern = LikePatternHelper.Contains(term.Trim());

        var tickets = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Select(t => new { t.Id, t.Title, t.ClientId, t.SiteId, t.CreatedAt, t.TemplateId });

        var query =
            from a in db.TicketAnswers.AsNoTracking()
            join t in tickets on a.TicketId equals t.Id
            where !a.IsSensitive
                  && a.ValueText != null
                  && (EF.Functions.ILike(a.QuestionLabel, pattern) || EF.Functions.ILike(a.ValueText, pattern))
            select new { a, t };

        if (!hasGlobalAccess)
        {
            query = query.Where(x =>
                allowedClients.Contains(x.t.ClientId) ||
                (x.t.SiteId.HasValue && allowedSites.Contains(x.t.SiteId.Value)));
        }

        if (templateId.HasValue)
            query = query.Where(x => x.t.TemplateId == templateId.Value);

        if (!string.IsNullOrWhiteSpace(questionKey))
        {
            var key = questionKey.Trim();
            query = query.Where(x => x.a.QuestionKey == key);
        }

        var rows = await query
            .OrderByDescending(x => x.t.CreatedAt)
            .Take(cap)
            .Select(x => new
            {
                x.t.Id,
                x.t.Title,
                x.t.ClientId,
                x.t.SiteId,
                x.t.CreatedAt,
                x.a.QuestionKey,
                x.a.QuestionLabel,
                ValueText = x.a.ValueText ?? string.Empty
            })
            .ToListAsync(ct);

        // Distância neutra (0) no modo keyword: a UI mostra "correspondência por texto".
        return rows
            .Select(x => new TicketAnswerSearchHit(
                x.Id, x.Title, x.ClientId, x.SiteId,
                x.QuestionKey, x.QuestionLabel, x.ValueText, 0d, x.CreatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<TicketAnswerSearchHit>> SearchSemanticAsync(
        Vector queryEmbedding,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        IReadOnlyCollection<Guid> allowedSiteIds,
        Guid? templateId,
        string? questionKey,
        int limit,
        double minSimilarity,
        CancellationToken ct = default)
    {
        if (!hasGlobalAccess && allowedClientIds.Count == 0 && allowedSiteIds.Count == 0)
            return Array.Empty<TicketAnswerSearchHit>();

        var cap = Math.Clamp(limit, 1, 50);
        var allowedClients = allowedClientIds.Distinct().ToArray();
        var allowedSites = allowedSiteIds.Distinct().ToArray();

        var tickets = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Select(t => new { t.Id, t.Title, t.ClientId, t.SiteId, t.CreatedAt, t.TemplateId });

        var query =
            from a in db.TicketAnswers.AsNoTracking()
            join t in tickets on a.TicketId equals t.Id
            where a.Embedding != null && !a.IsSensitive
            select new { a, t };

        // ACL no SQL (nunca em memória).
        if (!hasGlobalAccess)
        {
            query = query.Where(x =>
                allowedClients.Contains(x.t.ClientId) ||
                (x.t.SiteId.HasValue && allowedSites.Contains(x.t.SiteId.Value)));
        }

        if (templateId.HasValue)
            query = query.Where(x => x.t.TemplateId == templateId.Value);

        if (!string.IsNullOrWhiteSpace(questionKey))
        {
            var key = questionKey.Trim();
            query = query.Where(x => x.a.QuestionKey == key);
        }

        // Distância projetada uma única vez; busca um pouco além para aplicar
        // o corte de similaridade em memória (mesmo padrão da KB).
        var fetchLimit = minSimilarity > 0.0 ? Math.Min(cap * 2, cap + 10) : cap;

        var rows = await query
            .Select(x => new
            {
                x.t.Id,
                x.t.Title,
                x.t.ClientId,
                x.t.SiteId,
                x.t.CreatedAt,
                x.a.QuestionKey,
                x.a.QuestionLabel,
                ValueText = x.a.ValueText ?? string.Empty,
                Distance = (double)x.a.Embedding!.CosineDistance(queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .Take(fetchLimit)
            .ToListAsync(ct);

        var filtered = minSimilarity > 0.0
            ? rows.Where(x => 1.0 - x.Distance >= minSimilarity).Take(cap).ToList()
            : rows;

        return filtered
            .Select(x => new TicketAnswerSearchHit(
                x.Id, x.Title, x.ClientId, x.SiteId,
                x.QuestionKey, x.QuestionLabel, x.ValueText, x.Distance, x.CreatedAt))
            .ToList();
    }
}
