using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.Cqrs.Knowledge.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/knowledge")]
public class KnowledgeController(
    IMediator mediator,
    IAttachmentService attachmentService,
    IAttachmentRepository attachmentRepository,
    IKnowledgeArticleRepository articleRepository,
    IKnowledgeArticlePageRepository pageRepository) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [HttpGet]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] string? category = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null)
    {
        // Listagem unificada: sempre usa ACL multi-escopo do usuário.
        // clientId/siteId refinam o resultado (ex.: dropdown de cliente na UI).
        // sortBy: "title" (legacy) | "updatedAt"; sortDirection: asc | desc.
        // Clamp do limit: guard contra valores absurdos (DoS via Take ilimitado).
        limit = Math.Clamp(limit, 1, 200);
        var result = await mediator.Send(new ListKnowledgeArticlesByUserScopeQuery(
            cursor, limit, status, departmentId, category, clientId, siteId, sortBy, sortDirection));
        return result.ToActionResult();
    }

    [HttpGet("search")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] int maxResults = 10, [FromQuery] Guid? departmentId = null, [FromQuery] string mode = "hybrid")
    {
        // mode: semantic | keyword | hybrid (híbrido = semântico com fallback keyword).
        // departmentId restringe artigos Internal ao departamento.
        var result = await mediator.Send(new SearchKnowledgeQuery(q, clientId, siteId, maxResults, departmentId, mode));
        return result.ToActionResult();
    }

    /// <summary>
    /// Busca de sugestões com score (chunks semânticos) para IA/chat.
    /// Endpoint consumido pelo console web — antes não existia (404).
    /// </summary>
    [HttpPost("chat-search")]
    [RequirePermission(ResourceType.AiChat, ActionType.View)]
    public async Task<IActionResult> ChatSearch([FromBody] KbSearchRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new SearchKbSuggestionsQuery(request), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetKnowledgeArticleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateArticleRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateKnowledgeArticleCommand(
            request.Title, request.Content, request.Category, request.Tags,
            request.CreatedBy, request.ClientId, request.SiteId, request.DepartmentId), ct);
        return result.Match<IActionResult>(
            success: article => CreatedAtAction(nameof(GetById), new { id = article.Id, version = "v1" }, article),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateArticleRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdateKnowledgeArticleCommand(
            id, request.Title, request.Content, request.Category, request.Tags, request.LastEditedBy), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteKnowledgeArticleCommand(id), ct);
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("{id:guid}/publish")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> Publish(Guid id, [FromBody] PublishArticleRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new PublishKnowledgeArticleCommand(id, request.Status, request.LastEditedBy, request.ChangeSummary), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("{id:guid}/unpublish")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> Unpublish(Guid id, [FromQuery] string? lastEditedBy = null, CancellationToken ct = default)
    {
        var result = await mediator.Send(new UnpublishKnowledgeArticleCommand(id, lastEditedBy), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{id:guid}/versions")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> GetVersions(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetKnowledgeArticleVersionsQuery(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Sub-páginas internas do artigo (estilo Notion) ──────────────

    [HttpGet("{id:guid}/pages")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> GetPages(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetArticlePagesQuery(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{id:guid}/pages/{pageId:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> GetPage(Guid id, Guid pageId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetArticlePageQuery(id, pageId), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("{id:guid}/pages")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> CreatePage(Guid id, [FromBody] CreateArticlePageRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateArticlePageCommand(
            id, request.Title, request.Content, request.ParentPageId, request.SortOrder), ct);
        return result.Match<IActionResult>(
            success: page => CreatedAtAction(nameof(GetPage), new { id, pageId = page.Id, version = "v1" }, page),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("{id:guid}/pages/{pageId:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> UpdatePage(Guid id, Guid pageId, [FromBody] UpdateArticlePageRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdateArticlePageCommand(
            id, pageId, request.Title, request.Content, request.ParentPageId, request.SortOrder), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("{id:guid}/pages/{pageId:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> DeletePage(Guid id, Guid pageId, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteArticlePageCommand(id, pageId), ct);
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Imagens do artigo (privadas) ──────────────────────────────────
    //
    // O Markdown guarda ![alt](kbimage:<attachmentId>): referência estável, sem
    // URL pública e sem URL pré-assinada persistida (que expiraria junto com o
    // link). Toda leitura passa por endpoint autenticado com checagem de
    // proprietário (EntityType + EntityId): o bucket nunca é exposto. O console
    // resolve o token para uma URL pré-assinada CURTA no momento da exibição; o
    // agente tem rota agent-auth própria (bytes + cache offline).

    /// <summary>Prepara o PUT direto no storage para uma imagem do artigo.</summary>
    [HttpPost("{id:guid}/images/presigned-upload")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> PrepareImageUpload(Guid id, [FromBody] PresignedUploadRequestDto req, CancellationToken ct)
    {
        // Leitura leve: os endpoints de imagem só usam ClientId/Content/Id, nunca
        // os chunks/embeddings do artigo.
        var article = await articleRepository.GetWithoutChunksAsync(id, ct);
        if (article is null) return NotFound(new { error = "Artigo não encontrado." });

        if (req is null || string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.ContentType))
            return BadRequest(new { error = "Nome e tipo da imagem são obrigatórios." });

        if (!KnowledgeImageRules.IsAllowedContentType(req.ContentType))
            return BadRequest(new { error = $"Tipo de imagem não permitido: {req.ContentType}." });

        if (!KnowledgeImageRules.IsAllowedSize(req.SizeBytes))
            return BadRequest(new { error = $"Tamanho de imagem inválido. Máximo: {KnowledgeImageRules.MaxFileSizeBytes} bytes." });

        try
        {
            var result = await attachmentService.PreparePresignedUploadAsync(
                KnowledgeImageRules.EntityType,
                id,
                article.ClientId,
                req.FileName,
                req.ContentType,
                req.SizeBytes,
                KnowledgeImageRules.UploadUrlTtlMinutes,
                ct);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Único caso possível aqui é o storage não configurado (o tamanho já
            // foi validado acima) — o próprio provider devolve a mensagem.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>Finaliza o upload e persiste a imagem do artigo.</summary>
    [HttpPost("{id:guid}/images/complete-upload")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> CompleteImageUpload(Guid id, [FromBody] KnowledgeCompleteUploadRequestDto req, CancellationToken ct)
    {
        // Leitura leve: os endpoints de imagem só usam ClientId/Content/Id, nunca
        // os chunks/embeddings do artigo.
        var article = await articleRepository.GetWithoutChunksAsync(id, ct);
        if (article is null) return NotFound(new { error = "Artigo não encontrado." });

        if (req is null || req.AttachmentId == Guid.Empty || string.IsNullOrWhiteSpace(req.ObjectKey))
            return BadRequest(new { error = "Dados do upload incompletos." });

        if (!KnowledgeImageRules.IsAllowedContentType(req.ContentType))
            return BadRequest(new { error = $"Tipo de imagem não permitido: {req.ContentType}." });

        if (!KnowledgeImageRules.IsAllowedSize(req.SizeBytes))
            return BadRequest(new { error = $"Tamanho de imagem inválido. Máximo: {KnowledgeImageRules.MaxFileSizeBytes} bytes." });

        try
        {
            // Autor sempre o usuário autenticado (nunca o body).
            var attachment = await attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId,
                KnowledgeImageRules.EntityType,
                id,
                article.ClientId,
                req.FileName,
                req.ContentType,
                req.SizeBytes,
                req.ObjectKey,
                Username,
                ct);

            // O limite vale sobre o tamanho REAL no storage, não o declarado.
            if (attachment.SizeBytes > KnowledgeImageRules.MaxFileSizeBytes)
            {
                await attachmentService.DeleteAttachmentAsync(attachment.Id, ct);
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                    new { error = $"Imagem excede o tamanho máximo permitido ({KnowledgeImageRules.MaxFileSizeBytes} bytes)." });
            }

            return Ok(ToImageResponse(attachment));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Lista as imagens do artigo já com URL pré-assinada curta e o indicador de
    /// uso no conteúdo — é o que a galeria do editor e o viewer consomem.
    /// </summary>
    [HttpGet("{id:guid}/images")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> GetImages(Guid id, CancellationToken ct)
    {
        // Leitura leve: os endpoints de imagem só usam ClientId/Content/Id, nunca
        // os chunks/embeddings do artigo.
        var article = await articleRepository.GetWithoutChunksAsync(id, ct);
        if (article is null) return NotFound(new { error = "Artigo não encontrado." });

        var attachments = await attachmentService.GetAttachmentsForEntityAsync(KnowledgeImageRules.EntityType, id, ct);
        // inUse precisa olhar o artigo E as sub-páginas: a imagem pode ser usada
        // só numa página interna e, sem isso, apareceria como órfã.
        var referenced = await CollectReferencedImageIdsAsync(article, ct);

        var items = new List<KnowledgeImageResponseDto>(attachments.Count);
        foreach (var attachment in attachments)
        {
            string? url = null;
            DateTime? urlExpiresAtUtc = null;
            try
            {
                url = await attachmentService.GetPresignedDownloadUrlAsync(attachment, KnowledgeImageRules.ReadUrlTtlHours, ct);
                urlExpiresAtUtc = DateTime.UtcNow.AddHours(KnowledgeImageRules.ReadUrlTtlHours);
            }
            catch (Exception)
            {
                // Storage indisponível não pode derrubar a galeria: o item vai sem
                // URL e a UI mostra o placeholder.
            }

            items.Add(ToImageResponse(attachment, url, urlExpiresAtUtc, referenced.Contains(attachment.Id)));
        }

        return Ok(items);
    }

    /// <summary>Renova a URL pré-assinada de uma imagem (expirou na tela).</summary>
    [HttpGet("{id:guid}/images/{attachmentId:guid}/url")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> GetImageUrl(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await FindImageAsync(id, attachmentId, ct);
        if (attachment is null) return NotFound(new { error = "Imagem não encontrada neste artigo." });

        try
        {
            var url = await attachmentService.GetPresignedDownloadUrlAsync(attachment.Id, KnowledgeImageRules.ReadUrlTtlHours, ct);
            return Ok(new KnowledgeImageUrlDto(url, DateTime.UtcNow.AddHours(KnowledgeImageRules.ReadUrlTtlHours)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Qualquer falha do provider/storage vira 503 (a listagem e o stream
            // já fazem isso): 500 sugeriria bug da API para o cliente.
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Não foi possível gerar a URL da imagem." });
        }
    }

    /// <summary>
    /// Stream autenticado da imagem (fallback do viewer e base para o agente).
    /// Sem Authorization não há imagem: é isto que substitui a URL pública.
    /// </summary>
    [HttpGet("{id:guid}/images/{attachmentId:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.View)]
    public async Task<IActionResult> DownloadImage(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await FindImageAsync(id, attachmentId, ct);
        if (attachment is null) return NotFound();

        try
        {
            var stream = await attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;

            // Imagem privada: cache só no cliente, nunca em proxy compartilhado.
            Response.Headers["Cache-Control"] = "private, max-age=300";
            // O Content-Type vem do anexo (declarado no upload): sem nosniff um
            // arquivo poliglota poderia ser interpretado como outro tipo.
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, contentType);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar a imagem." });
        }
    }

    /// <summary>Exclui a imagem e, por padrão, a referência no conteúdo.</summary>
    [HttpDelete("{id:guid}/images/{attachmentId:guid}")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Edit)]
    public async Task<IActionResult> DeleteImage(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteKnowledgeImageCommand(id, attachmentId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// IDs de imagem referenciados no artigo + sub-páginas (campo inUse da galeria).
    /// </summary>
    private async Task<HashSet<Guid>> CollectReferencedImageIdsAsync(KnowledgeArticle article, CancellationToken ct)
    {
        var referenced = KnowledgeImageMarkdown.ExtractIds(article.Content).ToHashSet();
        var pages = await pageRepository.ListByArticleAsync(article.Id, ct);
        foreach (var page in pages)
        {
            foreach (var imageId in KnowledgeImageMarkdown.ExtractIds(page.Content))
                referenced.Add(imageId);
        }

        return referenced;
    }

    /// <summary>
    /// Exclusão DEFINITIVA de uma imagem: remove o objeto do storage agora e a
    /// linha do banco. Sem recuperação e revoga links pré-assinados vigentes —
    /// por isso exige permissão de exclusão (não a de edição).
    /// </summary>
    [HttpDelete("{id:guid}/images/{attachmentId:guid}/permanent")]
    [RequirePermission(ResourceType.KnowledgeBase, ActionType.Delete)]
    public async Task<IActionResult> PermanentlyDeleteImage(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var result = await mediator.Send(
            new DeleteKnowledgeImageCommand(id, attachmentId, Permanent: true), ct);

        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Carrega a imagem garantindo que pertence a ESTE artigo (anti-IDOR).</summary>
    private async Task<Attachment?> FindImageAsync(Guid articleId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, KnowledgeImageRules.EntityType, StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != articleId)
        {
            return null;
        }

        return attachment;
    }

    private static KnowledgeImageResponseDto ToImageResponse(
        Attachment attachment,
        string? url = null,
        DateTime? urlExpiresAtUtc = null,
        bool inUse = false)
        => new(
            attachment.Id,
            attachment.FileName,
            attachment.ContentType,
            attachment.SizeBytes,
            attachment.CreatedAt,
            attachment.UploadedBy,
            url,
            urlExpiresAtUtc,
            inUse);
}

/// <summary>Complete-upload de imagem (sem UploadedBy: o autor vem do token).</summary>
public record KnowledgeCompleteUploadRequestDto(
    Guid AttachmentId,
    string ObjectKey,
    string FileName,
    string ContentType,
    long SizeBytes);

/// <summary>Imagem do artigo devolvida ao console. Url é pré-assinada e curta.</summary>
public record KnowledgeImageResponseDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime CreatedAt,
    string? UploadedBy,
    string? Url,
    DateTime? UrlExpiresAtUtc,
    bool InUse);

/// <summary>URL pré-assinada de leitura renovada sob demanda.</summary>
public record KnowledgeImageUrlDto(string Url, DateTime ExpiresAtUtc);
