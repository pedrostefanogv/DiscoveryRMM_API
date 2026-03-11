using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Pgvector;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/knowledge")]
public class KnowledgeController(
    IKnowledgeArticleRepository articleRepository,
    IKnowledgeChunkRepository chunkRepository,
    IEmbeddingProvider embeddingProvider,
    IConfigurationResolver configurationResolver) : ControllerBase
{
    // ─── CRUD de Artigos ──────────────────────────────────────────────

    /// <summary>
    /// Lista artigos respeitando herança: site → client → global
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ArticleListItem>>> List(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] string? category,
        [FromQuery] bool publishedOnly = true,
        CancellationToken ct = default)
    {
        var articles = await articleRepository.ListByScopeAsync(clientId, siteId, publishedOnly, category, ct);
        var response = articles.Select(a => MapToListItem(a)).ToList();
        return Ok(response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ArticleResponse>> GetById(Guid id, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();
        return Ok(MapToResponse(article));
    }

    [HttpPost]
    public async Task<ActionResult<ArticleResponse>> Create(
        [FromBody] CreateArticleRequest request,
        CancellationToken ct = default)
    {
        // Valida escopo: site_id só pode existir se client_id também existir
        if (request.SiteId.HasValue && !request.ClientId.HasValue)
            return BadRequest("ClientId é obrigatório quando SiteId é informado.");

        var tagsJson = request.Tags?.Count > 0
            ? JsonSerializer.Serialize(request.Tags)
            : null;

        var article = new KnowledgeArticle
        {
            Title = request.Title.Trim(),
            Content = request.Content,
            Category = request.Category?.Trim(),
            TagsJson = tagsJson,
            Author = request.Author,
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            IsPublished = false
        };

        var created = await articleRepository.CreateAsync(article, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapToResponse(created));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ArticleResponse>> Update(
        Guid id,
        [FromBody] UpdateArticleRequest request,
        CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        article.Title = request.Title.Trim();
        article.Content = request.Content;
        article.Category = request.Category?.Trim();
        article.TagsJson = request.Tags?.Count > 0 ? JsonSerializer.Serialize(request.Tags) : null;
        article.Author = request.Author;
        // Invalida chunking para re-processar
        article.LastChunkedAt = null;

        var updated = await articleRepository.UpdateAsync(article, ct);
        return Ok(MapToResponse(updated));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();
        await articleRepository.DeleteAsync(id, ct);
        return NoContent();
    }

    // ─── Publicação ───────────────────────────────────────────────────

    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<ArticleResponse>> Publish(Guid id, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        article.IsPublished = true;
        article.PublishedAt ??= DateTime.UtcNow;
        // Força re-chunking ao publicar
        article.LastChunkedAt = null;

        var updated = await articleRepository.UpdateAsync(article, ct);
        return Ok(MapToResponse(updated));
    }

    [HttpPost("{id:guid}/unpublish")]
    public async Task<ActionResult<ArticleResponse>> Unpublish(Guid id, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        article.IsPublished = false;
        var updated = await articleRepository.UpdateAsync(article, ct);
        return Ok(MapToResponse(updated));
    }

    // ─── Busca ────────────────────────────────────────────────────────

    /// <summary>
    /// Busca unificada: semantic (pgvector), keyword (ILIKE) ou hybrid (ambos)
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<KbSearchResult>>> Search(
        [FromQuery] string q,
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] string mode = "hybrid",
        [FromQuery] int maxResults = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("q é obrigatório.");

        var results = new List<KbSearchResult>();

        if (mode is "semantic" or "hybrid")
        {
            var aiSettings = await configurationResolver.GetAISettingsAsync();
            var embedding = await embeddingProvider.GenerateEmbeddingAsync(
                q,
                aiSettings.EmbeddingModel,
                aiSettings.ApiKey,
                ct);
            var semanticResults = await chunkRepository.SearchSemanticAsync(
                new Vector(embedding), clientId, siteId, maxResults, ct);

            results.AddRange(semanticResults.Select(r => new KbSearchResult(
                r.ArticleId,
                r.ArticleTitle,
                r.SectionTitle,
                r.ChunkContent.Length > 400 ? r.ChunkContent[..400] + "..." : r.ChunkContent,
                null,
                GetScope(r.ArticleClientId, r.ArticleSiteId),
                r.ArticleClientId,
                r.ArticleSiteId,
                Math.Round(1.0 - r.Distance, 4))));
        }

        if (mode is "keyword" or "hybrid")
        {
            var keywordResults = await articleRepository.SearchKeywordAsync(q, clientId, siteId, ct);
            var existingIds = results.Select(r => r.ArticleId).ToHashSet();

            results.AddRange(keywordResults
                .Where(a => !existingIds.Contains(a.Id))
                .Take(maxResults)
                .Select(a => new KbSearchResult(
                    a.Id,
                    a.Title,
                    null,
                    a.Content.Length > 400 ? a.Content[..400] + "..." : a.Content,
                    a.Category,
                    GetScope(a.ClientId, a.SiteId),
                    a.ClientId,
                    a.SiteId,
                    null)));
        }

        // Ordenar: semânticos primeiro (por score desc), keyword depois
        var ordered = results
            .OrderByDescending(r => r.Score ?? 0)
            .Take(maxResults)
            .ToList();

        return Ok(ordered);
    }

    // ─── Vínculo Ticket ↔ KB (montado em /api/tickets/{ticketId}/knowledge) ──

    [HttpGet("/api/tickets/{ticketId:guid}/knowledge")]
    public async Task<ActionResult<List<TicketKnowledgeLinkResponse>>> GetTicketKnowledge(
        Guid ticketId, CancellationToken ct = default)
    {
        var links = await articleRepository.GetTicketLinksAsync(ticketId, ct);
        var response = links.Select(l => new TicketKnowledgeLinkResponse(
            l.Id,
            l.TicketId,
            l.ArticleId,
            l.Article.Title,
            l.Article.Category,
            l.LinkedBy,
            l.Note,
            l.LinkedAt)).ToList();
        return Ok(response);
    }

    [HttpPost("/api/tickets/{ticketId:guid}/knowledge")]
    public async Task<ActionResult<TicketKnowledgeLinkResponse>> LinkToTicket(
        Guid ticketId,
        [FromBody] LinkTicketRequest request,
        CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(request.ArticleId, ct);
        if (article == null) return NotFound("Artigo não encontrado.");

        var existing = await articleRepository.GetLinkAsync(ticketId, request.ArticleId, ct);
        if (existing != null) return Conflict("Artigo já está vinculado a este ticket.");

        var link = await articleRepository.LinkToTicketAsync(
            ticketId, request.ArticleId, request.LinkedBy, request.Note, ct);

        var response = new TicketKnowledgeLinkResponse(
            link.Id, link.TicketId, link.ArticleId,
            article.Title, article.Category,
            link.LinkedBy, link.Note, link.LinkedAt);

        return CreatedAtAction(nameof(GetTicketKnowledge), new { ticketId }, response);
    }

    [HttpDelete("/api/tickets/{ticketId:guid}/knowledge/{articleId:guid}")]
    public async Task<IActionResult> UnlinkFromTicket(
        Guid ticketId, Guid articleId, CancellationToken ct = default)
    {
        var existing = await articleRepository.GetLinkAsync(ticketId, articleId, ct);
        if (existing == null) return NotFound();
        await articleRepository.UnlinkFromTicketAsync(ticketId, articleId, ct);
        return NoContent();
    }

    /// <summary>
    /// Sugere artigos relevantes para um ticket via busca semântica no título+descrição
    /// </summary>
    [HttpGet("/api/tickets/{ticketId:guid}/knowledge/suggest")]
    public async Task<ActionResult<List<KbSearchResult>>> SuggestForTicket(
        Guid ticketId,
        [FromQuery] string q,
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] int maxResults = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("q (título ou descrição do ticket) é obrigatório.");

        var settings = await configurationResolver.GetAISettingsAsync();
        var embedding = await embeddingProvider.GenerateEmbeddingAsync(
            q,
            settings.EmbeddingModel,
            settings.ApiKey,
            ct);
        var semanticResults = await chunkRepository.SearchSemanticAsync(
            new Vector(embedding), clientId, siteId, maxResults, ct);

        var response = semanticResults.Select(r => new KbSearchResult(
            r.ArticleId,
            r.ArticleTitle,
            r.SectionTitle,
            r.ChunkContent.Length > 400 ? r.ChunkContent[..400] + "..." : r.ChunkContent,
            null,
            GetScope(r.ArticleClientId, r.ArticleSiteId),
            r.ArticleClientId,
            r.ArticleSiteId,
            Math.Round(1.0 - r.Distance, 4))).ToList();

        return Ok(response);
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static string GetScope(Guid? clientId, Guid? siteId) =>
        (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static List<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrEmpty(tagsJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? []; }
        catch { return []; }
    }

    private static ArticleListItem MapToListItem(KnowledgeArticle a) => new(
        a.Id, a.Title, a.Category, ParseTags(a.TagsJson), a.Author,
        GetScope(a.ClientId, a.SiteId), a.ClientId, a.SiteId,
        a.IsPublished, a.PublishedAt, a.Chunks.Count,
        a.CreatedAt, a.UpdatedAt);

    private static ArticleResponse MapToResponse(KnowledgeArticle a)
    {
        var chunks = a.Chunks.ToList();
        var embeddingsReady = chunks.Count > 0 && chunks.All(c => c.EmbeddingGeneratedAt != null);
        return new ArticleResponse(
            a.Id, a.Title, a.Content, a.Category, ParseTags(a.TagsJson), a.Author,
            GetScope(a.ClientId, a.SiteId), a.ClientId, a.SiteId,
            a.IsPublished, a.PublishedAt, chunks.Count, embeddingsReady,
            a.CreatedAt, a.UpdatedAt);
    }
}
