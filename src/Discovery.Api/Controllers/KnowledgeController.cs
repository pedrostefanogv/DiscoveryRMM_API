using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Identity;
using Microsoft.AspNetCore.Mvc;
using Pgvector;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/knowledge")]
public class KnowledgeController(
    IKnowledgeArticleRepository articleRepository,
    IKnowledgeChunkRepository chunkRepository,
    IEmbeddingProvider embeddingProvider,
    IConfigurationResolver configurationResolver,
    IKnowledgeEmbeddingQueueRepository embeddingQueueRepository,
    IUserRepository userRepository) : ControllerBase
{
    // ─── CRUD de Artigos ──────────────────────────────────────────────

    /// <summary>
    /// Lista artigos respeitando herança: site → client → global.
    /// Filtra por status, departamento e categoria.
    /// Artigos Internal só são visíveis se departmentId fornecido.
    /// </summary>
    [HttpGet]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<ActionResult<List<ArticleListItem>>> List(
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] string? status,
        [FromQuery] Guid? departmentId,
        [FromQuery] string? category,
        CancellationToken ct = default)
    {
        var articles = await articleRepository.ListByScopeAsync(
            clientId, siteId, status, departmentId, category, ct);
        var response = articles.Select(MapToListItem).ToList();
        return Ok(response);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<ActionResult<ArticleResponse>> GetById(Guid id, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();
        return Ok(MapToResponse(article));
    }

    /// <summary>
    /// Cria artigo em status Draft. O autor original fica em CreatedBy (imutável).
    /// </summary>
    [HttpPost]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Create)]
    public async Task<ActionResult<ArticleResponse>> Create(
        [FromBody] CreateArticleRequest request,
        CancellationToken ct = default)
    {
        // Valida escopo: site_id só pode existir se client_id também existir
        if (request.SiteId.HasValue && !request.ClientId.HasValue)
            return BadRequest("ClientId é obrigatório quando SiteId é informado.");

        // Valida departamento: se Internal precisará de departmentId (validação no publish)
        var tagsJson = request.Tags?.Count > 0
            ? JsonSerializer.Serialize(request.Tags)
            : null;

        var createdBy = await ResolveActorDisplayNameAsync(request.CreatedBy);

        var article = new KnowledgeArticle
        {
            Title = request.Title.Trim(),
            Content = request.Content,
            Category = request.Category?.Trim(),
            TagsJson = tagsJson,
            CreatedBy = createdBy,
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            DepartmentId = request.DepartmentId,
            Status = ArticleStatus.Draft.ToString(),
            CurrentVersionNumber = 0
        };

        var created = await articleRepository.CreateAsync(article, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapToResponse(created));
    }

    /// <summary>
    /// Atualiza artigo. Edições em Draft NÃO geram versão — alteram o registro diretamente.
    /// Se estava Published/Internal, volta para Draft (unpublish implícito).
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<ActionResult<ArticleResponse>> Update(
        Guid id,
        [FromBody] UpdateArticleRequest request,
        CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        var lastEditedBy = await ResolveActorDisplayNameAsync(request.LastEditedBy);

        article.Title = request.Title.Trim();
        article.Content = request.Content;
        article.Category = request.Category?.Trim();
        article.TagsJson = request.Tags?.Count > 0 ? JsonSerializer.Serialize(request.Tags) : null;
        article.LastEditedBy = lastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;

        // Se estava publicado/interno, volta para Draft (edição requer re-publicação)
        if (article.Status != ArticleStatus.Draft.ToString())
        {
            article.Status = ArticleStatus.Draft.ToString();
        }

        // Invalida chunking para re-processar quando publicado
        article.LastChunkedAt = null;

        var updated = await articleRepository.UpdateAsync(article, ct);
        return Ok(MapToResponse(updated));
    }

    /// <summary>
    /// Soft delete do artigo.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();
        await articleRepository.DeleteAsync(id, ct);
        return NoContent();
    }

    // ─── Publicação / Internalização (com versionamento) ──────────────

    /// <summary>
    /// Publica ou internaliza um artigo.
    /// Transição Draft → Published ou Draft → Internal gera snapshot de versão.
    /// DepartmentId é obrigatório para status Internal.
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<ActionResult<ArticleResponse>> Publish(
        Guid id,
        [FromBody] PublishArticleRequest request,
        CancellationToken ct = default)
    {
        if (request.Status != ArticleStatus.Published.ToString()
            && request.Status != ArticleStatus.Internal.ToString())
            return BadRequest("Status deve ser 'Published' ou 'Internal'.");

        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        // Valida departamento obrigatório para Internal
        if (request.Status == ArticleStatus.Internal.ToString() && !article.DepartmentId.HasValue)
            return BadRequest("DepartmentId é obrigatório para artigos Internal.");

        var wasAlreadyPublished = article.Status == ArticleStatus.Published.ToString()
            || article.Status == ArticleStatus.Internal.ToString();

        var lastEditedBy = await ResolveActorDisplayNameAsync(request.LastEditedBy);

        // Atualiza status
        article.Status = request.Status;
        article.LastEditedBy = lastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;

        if (!wasAlreadyPublished)
        {
            article.PublishedAt = DateTime.UtcNow;
        }

        // Incrementa versão e cria snapshot
        article.CurrentVersionNumber++;
        var version = new KnowledgeArticleVersion
        {
            ArticleId = article.Id,
            VersionNumber = article.CurrentVersionNumber,
            Title = article.Title,
            Content = article.Content,
            Category = article.Category,
            TagsJson = article.TagsJson,
            Status = article.Status,
            EditedBy = lastEditedBy,
            ChangeSummary = request.ChangeSummary
        };
        await articleRepository.CreateVersionAsync(version, ct);

        // Força re-chunking
        article.LastChunkedAt = null;

        var updated = await articleRepository.UpdateAsync(article, ct);
        await embeddingQueueRepository.EnqueueAsync(updated.Id, "publish", ct);
        return Ok(MapToResponse(updated));
    }

    /// <summary>
    /// Volta artigo para Draft sem gerar versão.
    /// </summary>
    [HttpPost("{id:guid}/unpublish")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<ActionResult<ArticleResponse>> Unpublish(
        Guid id,
        [FromQuery] string? lastEditedBy,
        CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        var resolvedLastEditedBy = await ResolveActorDisplayNameAsync(lastEditedBy);

        article.Status = ArticleStatus.Draft.ToString();
        article.LastEditedBy = resolvedLastEditedBy;
        article.LastEditedAt = DateTime.UtcNow;

        var updated = await articleRepository.UpdateAsync(article, ct);
        return Ok(MapToResponse(updated));
    }

    // ─── Versionamento ──────────────────────────────────────────────

    /// <summary>
    /// Lista versões de um artigo (mais recente primeiro).
    /// </summary>
    [HttpGet("{id:guid}/versions")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<ActionResult<List<ArticleVersionResponse>>> GetVersions(
        Guid id, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        var versions = await articleRepository.GetVersionsAsync(id, ct);
        var response = versions.Select(v => new ArticleVersionResponse(
            v.Id, v.ArticleId, v.VersionNumber,
            v.Title, v.Content, v.Category, ParseTags(v.TagsJson),
            v.Status, v.EditedBy, v.ChangeSummary, v.CreatedAt)).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Visualiza uma versão específica.
    /// </summary>
    [HttpGet("{id:guid}/versions/{versionNumber:int}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<ActionResult<ArticleVersionResponse>> GetVersion(
        Guid id, int versionNumber, CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(id, ct);
        if (article == null) return NotFound();

        var version = await articleRepository.GetVersionAsync(id, versionNumber, ct);
        if (version == null) return NotFound();

        return Ok(new ArticleVersionResponse(
            version.Id, version.ArticleId, version.VersionNumber,
            version.Title, version.Content, version.Category, ParseTags(version.TagsJson),
            version.Status, version.EditedBy, version.ChangeSummary, version.CreatedAt));
    }

    // ─── Busca ────────────────────────────────────────────────────────

    /// <summary>
    /// Busca unificada: semantic (pgvector), keyword (ILIKE) ou hybrid (ambos).
    /// Aceita departmentId para filtrar artigos Internal do departamento.
    /// </summary>
    [HttpGet("search")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<ActionResult<List<KbSearchResult>>> Search(
        [FromQuery] string q,
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] Guid? departmentId,
        [FromQuery] string mode = "hybrid",
        [FromQuery] int maxResults = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("q é obrigatório.");

        var results = new List<KbSearchResult>();

        if (mode is "semantic" or "hybrid")
        {
            var aiSettings = await configurationResolver.GetAISettingsAsync();
            var embBaseUrl1 = string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl) ? aiSettings.BaseUrl : aiSettings.EmbeddingBaseUrl;
            var embApiKey1 = string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey) ? aiSettings.ApiKey : aiSettings.EmbeddingApiKey;
            var embedding = await embeddingProvider.GenerateEmbeddingAsync(
                q,
                aiSettings.EmbeddingModel,
                embApiKey1,
                embBaseUrl1,
                ct);
            var semanticResults = await chunkRepository.SearchSemanticAsync(
                new Vector(embedding), clientId, siteId, maxResults,
                departmentId: departmentId, ct: ct);

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
            var keywordResults = await articleRepository.SearchKeywordAsync(
                q, clientId, siteId, departmentId, ct);
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

    // ─── Busca integrada com Chat/IA ──────────────────────────────────

    /// <summary>
    /// Busca semântica otimizada para o chat.
    /// Recebe a pergunta do usuário + contexto (clientId, siteId, departmentId)
    /// e retorna chunks relevantes para injeção no system prompt.
    /// </summary>
    [HttpPost("chat-search")]
    [RequirePermission(ResourceType.AiChat, ActionType.View)]
    public async Task<ActionResult<KbSuggestResult>> ChatSearch(
        [FromBody] KbSearchRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query é obrigatório.");

        var settings = await configurationResolver.GetAISettingsAsync();
        var embBaseUrl = string.IsNullOrWhiteSpace(settings.EmbeddingBaseUrl) ? settings.BaseUrl : settings.EmbeddingBaseUrl;
        var embApiKey = string.IsNullOrWhiteSpace(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey;

        var embedding = await embeddingProvider.GenerateEmbeddingAsync(
            request.Query,
            settings.EmbeddingModel,
            embApiKey,
            embBaseUrl,
            ct);

        var semanticResults = await chunkRepository.SearchSemanticAsync(
            new Vector(embedding), request.ClientId, request.SiteId,
            Math.Min(request.MaxResults, 10),
            minSimilarity: 0.7, // Similaridade mínima para chat
            departmentId: request.DepartmentId,
            ct: ct);

        var suggestions = semanticResults.Select(r => new KbSearchResult(
            r.ArticleId,
            r.ArticleTitle,
            r.SectionTitle,
            r.ChunkContent.Length > 400 ? r.ChunkContent[..400] + "..." : r.ChunkContent,
            null,
            GetScope(r.ArticleClientId, r.ArticleSiteId),
            r.ArticleClientId,
            r.ArticleSiteId,
            Math.Round(1.0 - r.Distance, 4))).ToList();

        return Ok(new KbSuggestResult(suggestions));
    }

    // ─── Vínculo Ticket ↔ KB (montado em /api/v1/tickets/{ticketId}/knowledge) ──

    [HttpGet("/api/v1/tickets/{ticketId:guid}/knowledge")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
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

    [HttpPost("/api/v1/tickets/{ticketId:guid}/knowledge")]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
    public async Task<ActionResult<TicketKnowledgeLinkResponse>> LinkToTicket(
        Guid ticketId,
        [FromBody] LinkTicketRequest request,
        CancellationToken ct = default)
    {
        var article = await articleRepository.GetByIdAsync(request.ArticleId, ct);
        if (article == null) return NotFound("Artigo não encontrado.");

        var existing = await articleRepository.GetLinkAsync(ticketId, request.ArticleId, ct);
        if (existing != null) return Conflict("Artigo já está vinculado a este ticket.");

        var linkedBy = await ResolveActorDisplayNameAsync(request.LinkedBy);

        var link = await articleRepository.LinkToTicketAsync(
            ticketId, request.ArticleId, linkedBy, request.Note, ct);

        var response = new TicketKnowledgeLinkResponse(
            link.Id, link.TicketId, link.ArticleId,
            article.Title, article.Category,
            link.LinkedBy, link.Note, link.LinkedAt);

        return CreatedAtAction(nameof(GetTicketKnowledge), new { ticketId }, response);
    }

    [HttpDelete("/api/v1/tickets/{ticketId:guid}/knowledge/{articleId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Delete)]
    public async Task<IActionResult> UnlinkFromTicket(
        Guid ticketId, Guid articleId, CancellationToken ct = default)
    {
        var existing = await articleRepository.GetLinkAsync(ticketId, articleId, ct);
        if (existing == null) return NotFound();
        await articleRepository.UnlinkFromTicketAsync(ticketId, articleId, ct);
        return NoContent();
    }

    /// <summary>
    /// Sugere artigos relevantes para um ticket via busca semântica no título+descrição.
    /// </summary>
    [HttpGet("/api/v1/tickets/{ticketId:guid}/knowledge/suggest")]
    [RequirePermission(ResourceType.AiChat, ActionType.View)]
    public async Task<ActionResult<List<KbSearchResult>>> SuggestForTicket(
        Guid ticketId,
        [FromQuery] string q,
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        [FromQuery] Guid? departmentId,
        [FromQuery] int maxResults = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("q (título ou descrição do ticket) é obrigatório.");

        var settings = await configurationResolver.GetAISettingsAsync();
        var embBaseUrl2 = string.IsNullOrWhiteSpace(settings.EmbeddingBaseUrl) ? settings.BaseUrl : settings.EmbeddingBaseUrl;
        var embApiKey2 = string.IsNullOrWhiteSpace(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey;
        var embedding = await embeddingProvider.GenerateEmbeddingAsync(
            q,
            settings.EmbeddingModel,
            embApiKey2,
            embBaseUrl2,
            ct);
        var semanticResults = await chunkRepository.SearchSemanticAsync(
            new Vector(embedding), clientId, siteId, maxResults,
            departmentId: departmentId, ct: ct);

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

    /// <summary>
    /// Registra feedback (útil / não útil) para um artigo vinculado a um ticket.
    /// </summary>
    [HttpPost("/api/v1/tickets/{ticketId:guid}/knowledge/{articleId:guid}/feedback")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SetKbLinkFeedback(
        Guid ticketId,
        Guid articleId,
        [FromBody] Discovery.Core.DTOs.KbLinkFeedbackRequest request,
        CancellationToken ct = default)
    {
        var link = await articleRepository.GetLinkAsync(ticketId, articleId, ct);
        if (link is null) return NotFound("Vínculo não encontrado.");

        link.FeedbackUseful = request.Useful;
        link.FeedbackAt = DateTime.UtcNow;

        await articleRepository.UpdateLinkAsync(link, ct);

        return Ok(new
        {
            ticketId,
            articleId,
            link.FeedbackUseful,
            link.FeedbackAt
        });
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
        a.Id, a.Title, a.Category, ParseTags(a.TagsJson),
        a.CreatedBy, a.LastEditedBy,
        a.Status,
        GetScope(a.ClientId, a.SiteId), a.ClientId, a.SiteId,
        a.DepartmentId, a.CurrentVersionNumber,
        a.PublishedAt, a.Chunks.Count,
        a.CreatedAt, a.UpdatedAt);

    private static ArticleResponse MapToResponse(KnowledgeArticle a)
    {
        var chunks = a.Chunks.ToList();
        var embeddingsReady = chunks.Count > 0 && chunks.All(c => c.EmbeddingGeneratedAt != null);
        return new ArticleResponse(
            a.Id, a.Title, a.Content, a.Category, ParseTags(a.TagsJson),
            a.CreatedBy, a.LastEditedBy, a.LastEditedAt,
            a.Status,
            GetScope(a.ClientId, a.SiteId), a.ClientId, a.SiteId,
            a.DepartmentId, a.CurrentVersionNumber,
            a.PublishedAt, chunks.Count, embeddingsReady,
            a.CreatedAt, a.UpdatedAt);
    }

    private async Task<string> ResolveActorDisplayNameAsync(string? fallback)
    {
        var normalizedFallback = NormalizeActor(fallback);

        if (HttpContext.Items["UserId"] is not Guid userId)
        {
            return normalizedFallback ?? "system";
        }

        var user = await userRepository.GetByIdAsync(userId);
        var resolved = NormalizeActor(user?.FullName)
            ?? NormalizeActor(user?.Login)
            ?? NormalizeActor(user?.Email)
            ?? normalizedFallback
            ?? userId.ToString("D");

        return resolved;
    }

    private static string? NormalizeActor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }
}
