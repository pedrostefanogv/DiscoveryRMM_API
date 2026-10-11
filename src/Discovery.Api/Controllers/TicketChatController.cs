using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;
using Discovery.Core.Cqrs.Tickets.Chat.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

/// <summary>
/// Chat de suporte vinculado ao chamado (lado do atendente, console web).
///
/// Regra do recurso: SOMENTE este controller cria sessão. O lado agent-auth só
/// responde ao convite, envia mensagens do usuário final e encerra a própria
/// sessão — nunca abre uma conversa por iniciativa da máquina.
///
/// Entrega em tempo real: as mensagens chegam pelo NATS (canal de dashboard,
/// sem corpo) e o conteúdo é buscado aqui por REST (`?afterSequence=`).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/{ticketId:guid}/chat")]
public class TicketChatController(
    IMediator mediator,
    ITicketAccessGuard accessGuard,
    ITicketQueryService ticketQueryService,
    IAttachmentService attachmentService,
    IAttachmentRepository attachmentRepository) : ControllerBase
{
    private const string ChatEntityType = "TicketChat";

    private string CurrentUserName => User?.Identity?.Name
        ?? (HttpContext.Items["Username"] as string)
        ?? "suporte";

    private Guid? CurrentUserId => HttpContext.Items["UserId"] as Guid?;

    // ── Estado e histórico ────────────────────────────────────────────────

    /// <summary>Sessão pendente/ativa do chamado (HasSession=false quando não há).</summary>
    [HttpGet("sessions/current")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetCurrentSession(Guid ticketId, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.View, ct))
            return NotFound();

        var result = await mediator.Send(new GetTicketChatStateQuery(ticketId), ct);
        return result.ToActionResult();
    }

    /// <summary>Conversas anteriores do chamado (somente leitura).</summary>
    [HttpGet("sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetHistory(Guid ticketId, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.View, ct))
            return NotFound();

        var result = await mediator.Send(new GetTicketChatHistoryQuery(ticketId, skip, take), ct);
        return result.ToActionResult();
    }

    /// <summary>Snapshot incremental das mensagens de uma sessão do chamado.</summary>
    [HttpGet("sessions/{sessionId:guid}/messages")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetMessages(Guid ticketId, Guid sessionId, [FromQuery] long afterSequence = 0, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.View, ct))
            return NotFound();

        var (ownershipError, session) = await ResolveSessionAsync(ticketId, sessionId, ct);
        if (ownershipError is not null)
            return ownershipError;

        var result = await mediator.Send(new GetTicketChatMessagesQuery(sessionId, afterSequence, limit), ct);
        return result.ToActionResult();
    }

    // ── Ações do atendente ────────────────────────────────────────────────

    /// <summary>Inicia a sessão de chat e envia o convite à máquina do chamado.</summary>
    [HttpPost("sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> StartSession(Guid ticketId, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.Edit, ct))
            return NotFound();

        var result = await mediator.Send(new StartTicketChatSessionCommand(ticketId, CurrentUserId, CurrentUserName), ct);
        if (result.IsFailure)
        {
            var error = result.Errors[0];
            if (string.Equals(error.Code, "Conflict", StringComparison.Ordinal))
                return Conflict(new { code = error.Message, error = DescribeConflict(error.Message) });

            return result.ToActionResult();
        }

        return Ok(result.Value);
    }

    /// <summary>Reenvia o convite de uma sessão pendente.</summary>
    [HttpPost("sessions/{sessionId:guid}/invite/retry")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> RetryInvite(Guid ticketId, Guid sessionId, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.Edit, ct))
            return NotFound();

        var (ownershipError, session) = await ResolveSessionAsync(ticketId, sessionId, ct);
        if (ownershipError is not null)
            return ownershipError;

        var result = await mediator.Send(new RetryTicketChatInviteCommand(sessionId), ct);
        if (result.IsFailure)
        {
            var error = result.Errors[0];
            if (string.Equals(error.Code, "Conflict", StringComparison.Ordinal))
                return Conflict(new { code = error.Message, error = DescribeConflict(error.Message) });
            if (string.Equals(error.Code, "TooManyRequests", StringComparison.Ordinal))
                return StatusCode(StatusCodes.Status429TooManyRequests, new { code = error.Message, error = "Aguarde alguns segundos antes de reenviar o convite." });
        }

        return result.ToActionResult();
    }

    /// <summary>Envia uma mensagem do atendente.</summary>
    [HttpPost("sessions/{sessionId:guid}/messages")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SendMessage(Guid ticketId, Guid sessionId, [FromBody] SendTicketChatMessageRequest request, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.Edit, ct))
            return NotFound();

        var (ownershipError, session) = await ResolveSessionAsync(ticketId, sessionId, ct);
        if (ownershipError is not null)
            return ownershipError;

        var result = await mediator.Send(new SendTicketChatMessageCommand(
            sessionId,
            CurrentUserId,
            CurrentUserName,
            "support",
            request?.Body ?? string.Empty,
            request?.AttachmentIds,
            request?.ClientMessageId), ct);

        if (result.IsFailure && string.Equals(result.Errors[0].Code, "Conflict", StringComparison.Ordinal))
            return Conflict(new { code = result.Errors[0].Message, error = "A conversa não está ativa." });

        return result.ToActionResult();
    }

    /// <summary>Encerra a sessão pelo suporte.</summary>
    [HttpPost("sessions/{sessionId:guid}/end")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> EndSession(Guid ticketId, Guid sessionId, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.Edit, ct))
            return NotFound();

        var (ownershipError, session) = await ResolveSessionAsync(ticketId, sessionId, ct);
        if (ownershipError is not null)
            return ownershipError;

        var result = await mediator.Send(new EndTicketChatSessionCommand(sessionId, "support", "support_closed"), ct);
        return result.ToActionResult();
    }

    // ── Anexos (fluxo presigned já existente) ─────────────────────────────

    /// <summary>Prepara o upload direto no object storage para um anexo do chat.</summary>
    [HttpPost("sessions/{sessionId:guid}/attachments/presigned-upload")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> PrepareAttachmentUpload(Guid ticketId, Guid sessionId, [FromBody] PresignedUploadRequestDto req, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.Edit, ct))
            return NotFound();

        var (ownershipError, session) = await ResolveSessionAsync(ticketId, sessionId, ct);
        if (ownershipError is not null)
            return ownershipError;

        if (RequireActiveSession(session) is { } inactive)
            return inactive;

        var ticket = await ticketQueryService.GetTicketByIdAsync(ticketId, ct);
        if (ticket is null)
            return NotFound();

        var settingsResult = await mediator.Send(new GetTicketAttachmentSettingsQuery(), ct);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos estão desabilitados nas configurações do servidor." });

        if (req is null || string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.ContentType))
            return BadRequest(new { error = "Nome e tipo do arquivo são obrigatórios." });

        if (!settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxFileSizeBytes} bytes." });

        var result = await attachmentService.PreparePresignedUploadAsync(
            ChatEntityType, ticketId, ticket.ClientId, req.FileName, req.ContentType, req.SizeBytes,
            settings.PresignedUploadUrlTtlMinutes, ct);

        return Ok(result);
    }

    /// <summary>Finaliza o upload pré-assinado e persiste o anexo do chat.</summary>
    [HttpPost("sessions/{sessionId:guid}/attachments/complete-upload")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CompleteAttachmentUpload(Guid ticketId, Guid sessionId, [FromBody] CompleteUploadRequestDto req, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.Edit, ct))
            return NotFound();

        var (ownershipError, session) = await ResolveSessionAsync(ticketId, sessionId, ct);
        if (ownershipError is not null)
            return ownershipError;

        if (RequireActiveSession(session) is { } inactive)
            return inactive;

        var ticket = await ticketQueryService.GetTicketByIdAsync(ticketId, ct);
        if (ticket is null)
            return NotFound();

        var settingsResult = await mediator.Send(new GetTicketAttachmentSettingsQuery(), ct);
        if (settingsResult.IsFailure || settingsResult.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });
        var settings = settingsResult.Value;

        if (!settings.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Anexos estão desabilitados nas configurações do servidor." });

        if (req is null || !settings.IsContentTypeAllowed(req.ContentType))
            return BadRequest(new { error = $"Tipo de arquivo não permitido: {req?.ContentType}." });

        if (req.SizeBytes <= 0 || req.SizeBytes > settings.MaxFileSizeBytes)
            return BadRequest(new { error = $"Tamanho de arquivo inválido. Máximo permitido: {settings.MaxFileSizeBytes} bytes." });

        try
        {
            var attachment = await attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId, ChatEntityType, ticketId, ticket.ClientId,
                req.FileName, req.ContentType, req.SizeBytes, req.ObjectKey,
                CurrentUserName, ct);

            if (attachment.SizeBytes > settings.MaxFileSizeBytes)
            {
                await attachmentService.DeleteAttachmentAsync(attachment.Id, ct);
                return StatusCode(StatusCodes.Status413RequestEntityTooLarge, new { error = $"Arquivo excede o tamanho máximo permitido ({settings.MaxFileSizeBytes} bytes)." });
            }

            return Ok(attachment);
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

    /// <summary>Baixa um anexo do chat do chamado (streaming same-origin).</summary>
    [HttpGet("attachments/{attachmentId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> DownloadAttachment(Guid ticketId, Guid attachmentId, CancellationToken ct)
    {
        if (!await accessGuard.CanAccessTicketAsync(ticketId, ActionType.View, ct))
            return NotFound();

        var attachment = await attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, ChatEntityType, StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != ticketId)
        {
            return NotFound();
        }

        try
        {
            var stream = await attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;
            return File(stream, contentType, attachment.FileName);
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
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar o arquivo." });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Confirma que a sessão pertence a ESTE chamado (evita ler/escrever a
    /// conversa de outro chamado com um id de sessão vazado) e devolve a sessão.
    /// </summary>
    private async Task<(IActionResult? Error, TicketChatSessionDto? Session)> ResolveSessionAsync(Guid ticketId, Guid sessionId, CancellationToken ct)
    {
        var state = await mediator.Send(new GetTicketChatSessionQuery(sessionId), ct);
        var session = state.IsSuccess ? state.Value?.Session : null;

        if (session is null || session.TicketId != ticketId)
            return (NotFound(), null);

        return (null, session);
    }

    /// <summary>
    /// Anexo só entra em conversa ATIVA: evita arquivos órfãos no storage para
    /// uma sessão que já terminou (a UI já bloqueia, isto é defesa no servidor).
    /// </summary>
    private IActionResult? RequireActiveSession(TicketChatSessionDto? session)
        => string.Equals(session?.Status, "Active", StringComparison.OrdinalIgnoreCase)
            ? null
            : Conflict(new { code = "session_not_active", error = "A conversa não está ativa." });

    private static string DescribeConflict(string code) => code switch
    {
        "ticket_has_no_agent" => "O chamado não está vinculado a uma máquina.",
        "agent_offline" => "A máquina do chamado está offline; não é possível abrir o chat agora.",
        "agent_busy" => "A máquina já está em outro chat de suporte.",
        "session_not_pending" => "Esta conversa não está mais aguardando confirmação.",
        "invite_expired" => "O convite expirou. Inicie uma nova conversa.",
        "invite_cooldown" => "Aguarde alguns segundos antes de reenviar o convite.",
        "session_not_active" => "A conversa não está ativa.",
        _ => "Não foi possível concluir a operação."
    };
}

public record SendTicketChatMessageRequest(
    string Body,
    IReadOnlyList<Guid>? AttachmentIds = null,
    Guid? ClientMessageId = null);
