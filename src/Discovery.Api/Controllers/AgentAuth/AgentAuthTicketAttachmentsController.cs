using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public class AgentAuthTicketAttachmentsController : AgentAuthControllerBase
{
    private readonly IAttachmentService _attachmentService;
    private readonly IAttachmentRepository _attachmentRepository;
    private readonly INotificationService _notificationService;
    private readonly ILogger<AgentAuthTicketAttachmentsController> _logger;

    /// <summary>EntityType dos anexos de comentario na tabela attachments.</summary>
    private const string CommentEntityType = "TicketComment";

    public AgentAuthTicketAttachmentsController(
        IMediator mediator,
        IAgentRepository agentRepo,
        IAttachmentService attachmentService,
        IAttachmentRepository attachmentRepository,
        INotificationService notificationService,
        ILogger<AgentAuthTicketAttachmentsController> logger)
        : base(mediator, agentRepo)
    {
        _attachmentService = attachmentService;
        _attachmentRepository = attachmentRepository;
        _notificationService = notificationService;
        _logger = logger;
    }

    // ── Anexos de chamados (agent) ─────────────────────────────────────────
    //
    // O agent precisa anexar arquivos/prints ao comentar um chamado. As rotas
    // do console (/tickets/{id}/attachments/*) exigem permissao de usuario,
    // entao o equivalente existe aqui, com isolamento por Ticket.AgentId (IDOR)
    // e as MESMAS regras de TicketAttachmentSettings usadas no console/chat.

    /// <summary>Configuracao de anexos (tipos/tamanho) para a UI do agent.</summary>
    [HttpGet("me/tickets/attachment-settings")]
    public async Task<IActionResult> GetMyTicketAttachmentSettings()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var result = await _mediator.Send(new GetTicketAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (result.IsFailure || result.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });

        var settings = result.Value;
        return Ok(new
        {
            enabled = settings.Enabled,
            allowedContentTypes = settings.AllowedContentTypes,
            maxFileSizeBytes = settings.MaxFileSizeBytes,
            // Repositório de arquivos grandes do chamado (aba Anexos).
            maxLargeFileSizeBytes = settings.MaxLargeFileSizeBytes,
        });
    }

    /// <summary>Lista os anexos do chamado (somente do proprio agent).</summary>
    [HttpGet("me/tickets/{ticketId:guid}/attachments")]
    public async Task<IActionResult> GetMyTicketAttachments(
        Guid ticketId,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (await ResolveMyTicketAsync(id, ticketId) is null) return NotFound();

        var result = await _mediator.Send(new GetTicketAttachmentsQuery(ticketId, cursor, limit), HttpContext.RequestAborted);
        return MapResult(result, Ok);
    }

    /// <summary>Lista consolidada e paginada (repositório + conversa) do agent.</summary>
    [HttpGet("me/tickets/{ticketId:guid}/attachments/all")]
    public async Task<IActionResult> GetMyTicketAttachmentsAll(
        Guid ticketId,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (await ResolveMyTicketAsync(id, ticketId) is null) return NotFound();

        var result = await _mediator.Send(
            new GetTicketAttachmentsAllQuery(ticketId, cursor, limit), HttpContext.RequestAborted);
        return MapResult(result, Ok);
    }

    /// <summary>Baixa um anexo do chamado (stream same-origin autenticado).</summary>
    [HttpGet("me/tickets/{ticketId:guid}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadMyTicketAttachment(Guid ticketId, Guid attachmentId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (await ResolveMyTicketAsync(id, ticketId) is null) return NotFound();

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, "Ticket", StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != ticketId)
        {
            return NotFound();
        }

        try
        {
            var stream = await _attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;
            return File(stream, contentType, attachment.FileName);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar o arquivo." });
        }
    }

    /// <summary>Prepara o upload direto no storage (URL pre-assinada).</summary>
    [HttpPost("me/tickets/{ticketId:guid}/attachments/presigned-upload")]
    public async Task<IActionResult> PrepareMyTicketAttachmentUpload(
        Guid ticketId,
        [FromBody] PresignedUploadRequestDto req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetTicketAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos de chamados estão desabilitados nas configurações do servidor." });

        if (req is null || string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.ContentType))
            return BadRequest(new { error = "Nome e tipo do arquivo são obrigatórios." });

        if (!settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req.ContentType}." });

        // Anexo do CHAMADO = repositório de arquivos grandes (aba Anexos).
        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxLargeFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxLargeFileSizeBytes} bytes." });

        var result = await _attachmentService.PreparePresignedUploadAsync(
            "Ticket", ticketId, ticket.ClientId, req.FileName, req.ContentType, req.SizeBytes,
            settings.PresignedUploadUrlTtlMinutes, HttpContext.RequestAborted);

        return Ok(result);
    }

    /// <summary>Finaliza o upload pre-assinado e persiste o anexo do chamado.</summary>
    [HttpPost("me/tickets/{ticketId:guid}/attachments/complete-upload")]
    public async Task<IActionResult> CompleteMyTicketAttachmentUpload(
        Guid ticketId,
        [FromBody] AgentCompleteUploadRequest req,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetTicketAttachmentSettingsQuery(), ct);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos de chamados estão desabilitados nas configurações do servidor." });

        if (req is null || !settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req?.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxLargeFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxLargeFileSizeBytes} bytes." });

        try
        {
            // Autor sempre derivado do agent autenticado (nunca do body).
            var attachment = await _attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId, "Ticket", ticketId, ticket.ClientId,
                req.FileName, req.ContentType, req.SizeBytes, req.ObjectKey,
                $"agent:{id}", ct);

            // O limite vale sobre o tamanho REAL no storage (nao o declarado).
            if (attachment.SizeBytes > settings.MaxLargeFileSizeBytes)
            {
                await _attachmentService.DeleteAttachmentAsync(attachment.Id, ct);
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge, new { error = $"Arquivo excede o tamanho máximo permitido ({settings.MaxLargeFileSizeBytes} bytes)." });
            }

            // Anexo de REPOSITORIO enviado pelo agent: avisa o console (badge).
            await NotifyRepositoryAttachmentAsync(id, ticket, attachment.Id, attachment.FileName, attachment.SizeBytes);

            return Ok(attachment);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Notifica o console (badge no sino) que o agent anexou um arquivo ao
    /// repositorio do chamado. A notificacao vai para a chave compartilhada
    /// "tickets" (fila do time): o contrato publico do agent nao expoe o
    /// responsavel do chamado, e o payload identifica o chamado para o deep
    /// link. O binario nunca entra no payload.
    /// </summary>
    private async Task NotifyRepositoryAttachmentAsync(
        Guid agentId, AgentTicketDto ticket, Guid attachmentId, string fileName, long sizeBytes)
    {
        try
        {
            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket.attachment.added",
                Topic: "tickets",
                Title: "Novo anexo enviado pelo cliente",
                Message: $"{ticket.Title}: {fileName}",
                Severity: NotificationSeverity.Informational,
                Payload: new
                {
                    ticketId = ticket.Id,
                    attachmentId,
                    fileName,
                    sizeBytes,
                    origin = "agent"
                },
                RecipientKey: "tickets",
                CreatedBy: $"agent:{agentId}"));
        }
        catch (Exception ex)
        {
            // Best-effort: o upload ja esta persistido.
            _logger.LogWarning(ex, "Falha ao notificar anexo do chamado {TicketId}", ticket.Id);
        }
    }

    // ── Anexos de comentario (nivel conversa, no agent) ────────────────────
    //
    // Mesmo modelo do console: anexo de comentario e pequeno (10 MB por padrao,
    // imagens/PDF, com miniatura) e fica ligado ao COMENTARIO, nao ao chamado.
    // A posse e validada duas vezes: o chamado tem de ser do agent e o
    // comentario tem de pertencer a esse chamado.

    /// <summary>Limites/tipos do anexo de comentario para a UI do agent.</summary>
    [HttpGet("me/tickets/comment-attachment-settings")]
    public async Task<IActionResult> GetMyCommentAttachmentSettings()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var result = await _mediator.Send(new GetCommentAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (result.IsFailure || result.Value is null)
            return BadRequest(new { error = "Nao foi possivel ler a configuracao de anexos de comentario." });

        var settings = result.Value;
        return Ok(new
        {
            enabled = settings.Enabled,
            allowedContentTypes = settings.AllowedContentTypes,
            maxFileSizeBytes = settings.MaxFileSizeBytes,
        });
    }

    /// <summary>Valida chamado do agent + comentario pertencente a esse chamado.</summary>
    private async Task<bool> CommentBelongsToMyTicketAsync(Guid agentId, Guid ticketId, Guid commentId)
    {
        if (await ResolveMyTicketAsync(agentId, ticketId) is null) return false;

        var result = await _mediator.Send(new GetTicketCommentQuery(ticketId, commentId), HttpContext.RequestAborted);
        return result.IsSuccess && result.Value is not null;
    }

    /// <summary>Prepara o upload direto no storage para um anexo de comentario.</summary>
    [HttpPost("me/tickets/{ticketId:guid}/comments/{commentId:guid}/attachments/presigned-upload")]
    public async Task<IActionResult> PrepareMyCommentAttachmentUpload(
        Guid ticketId, Guid commentId, [FromBody] PresignedUploadRequestDto req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        if (!await CommentBelongsToMyTicketAsync(id, ticketId, commentId)) return NotFound();
        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetCommentAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Nao foi possivel ler a configuracao de anexos de comentario." });
        var settings = settingsResult.Value;

        if (req is null || string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.ContentType))
            return BadRequest(new { error = "Nome e tipo do arquivo sao obrigatorios." });

        if (!settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo nao permitido em comentarios: {req.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Anexo de comentario excede o limite de {settings.MaxFileSizeBytes} bytes." });

        var result = await _attachmentService.PreparePresignedUploadAsync(
            CommentEntityType, commentId, ticket.ClientId, req.FileName, req.ContentType,
            req.SizeBytes, settings.PresignedUploadUrlTtlMinutes, HttpContext.RequestAborted);

        return Ok(result);
    }

    /// <summary>Finaliza o upload pre-assinado e persiste o anexo do comentario.</summary>
    [HttpPost("me/tickets/{ticketId:guid}/comments/{commentId:guid}/attachments/complete-upload")]
    public async Task<IActionResult> CompleteMyCommentAttachmentUpload(
        Guid ticketId, Guid commentId, [FromBody] AgentCompleteUploadRequest req, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        if (!await CommentBelongsToMyTicketAsync(id, ticketId, commentId)) return NotFound();
        var ticket = await ResolveMyTicketAsync(id, ticketId);
        if (ticket is null) return NotFound();

        var settingsResult = await _mediator.Send(new GetCommentAttachmentSettingsQuery(), ct);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Nao foi possivel ler a configuracao de anexos de comentario." });
        var settings = settingsResult.Value;

        if (req is null || !settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo nao permitido em comentarios: {req?.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Anexo de comentario excede o limite de {settings.MaxFileSizeBytes} bytes." });

        try
        {
            // Autor sempre derivado do agent autenticado (nunca do body).
            var attachment = await _attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId, CommentEntityType, commentId, ticket.ClientId,
                req.FileName, req.ContentType, req.SizeBytes, req.ObjectKey, $"agent:{id}", ct);

            if (attachment.SizeBytes > settings.MaxFileSizeBytes)
            {
                await _attachmentService.DeleteAttachmentAsync(attachment.Id, ct);
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                    new { error = $"Arquivo excede o limite de anexo de comentario ({settings.MaxFileSizeBytes} bytes)." });
            }

            return Ok(attachment);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Baixa um anexo de comentario do chamado do proprio agent.</summary>
    [HttpGet("me/tickets/{ticketId:guid}/comments/{commentId:guid}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadMyCommentAttachment(
        Guid ticketId, Guid commentId, Guid attachmentId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        if (!await CommentBelongsToMyTicketAsync(id, ticketId, commentId)) return NotFound();

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, CommentEntityType, StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != commentId)
        {
            return NotFound();
        }

        try
        {
            var stream = await _attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;
            return File(stream, contentType, attachment.FileName);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Nao foi possivel baixar o arquivo." });
        }
    }

    /// <summary>
    /// Ticket do proprio agent (isolamento por AgentId). Devolve null quando o
    /// ticket nao existe OU pertence a outro agent — sem revelar a existencia.
    /// </summary>
    private async Task<AgentTicketDto?> ResolveMyTicketAsync(Guid agentId, Guid ticketId)
    {
        var result = await _mediator.Send(new GetMyTicketQuery(agentId, ticketId), HttpContext.RequestAborted);
        return result.IsSuccess ? result.Value : null;
    }
}
