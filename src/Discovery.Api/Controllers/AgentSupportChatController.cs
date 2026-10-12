using Discovery.Api;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;
using Discovery.Core.Cqrs.Tickets.Chat.Queries;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Chat de suporte — lado do agent (máquina do usuário final).
///
/// Este controller NÃO cria sessão: ele só responde ao convite, envia as
/// mensagens do usuário final, encerra a própria sessão e faz o upload dos
/// anexos. Toda rota valida que a sessão pertence ao agent autenticado.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/agent-auth/me/support-chat")]
[AllowAnonymous]
public class AgentSupportChatController(
    IMediator mediator,
    ITicketQueryService ticketQueryService,
    ITicketChatRepository chatRepository,
    IAttachmentService attachmentService,
    IAttachmentRepository attachmentRepository,
    ILogger<AgentSupportChatController> logger) : ControllerBase
{
    private const string ChatEntityType = "TicketChat";

    /// <summary>Anexos de conversa AVULSA (sem chamado) ficam ligados à sessão.</summary>
    private const string AdHocChatEntityType = "SupportChat";

    private bool TryGetAgentId(out Guid agentId)
    {
        if (HttpContext.Items["AgentId"] is Guid id)
        {
            agentId = id;
            return true;
        }

        agentId = Guid.Empty;
        return false;
    }

    private IActionResult MapResult<T>(Result<T> result, Func<T, IActionResult> onSuccess)
        where T : notnull
        => result.Match(onSuccess, errors =>
        {
            var error = errors[0];
            return error.Code switch
            {
                "NotFound" => NotFound(new { error = error.Message }),
                "Forbidden" => StatusCode(403, new { error = error.Message }),
                "Conflict" => Conflict(new { code = error.Message, error = "Operação inválida para o estado atual da conversa." }),
                "Validation" => BadRequest(new { error = error.Message, field = error.Field }),
                "Internal" => StatusCode(500, new { error = "Erro interno do servidor" }),
                _ => BadRequest(new { error = error.Message })
            };
        });

    /// <summary>Sessão ativa/pendente deste agent (usado no startup para retomar o chat).</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActive(CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var result = await mediator.Send(new GetAgentActiveChatQuery(agentId), ct);
        return MapResult(result, Ok);
    }

    /// <summary>Snapshot incremental das mensagens da própria sessão.</summary>
    [HttpGet("sessions/{sessionId:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid sessionId, [FromQuery] long afterSequence = 0, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var ownership = await ResolveOwnedSessionAsync(sessionId, agentId, ct);
        if (ownership.Error is not null)
            return ownership.Error;

        var result = await mediator.Send(new GetTicketChatMessagesQuery(sessionId, afterSequence, limit), ct);
        return MapResult(result, Ok);
    }

    /// <summary>Envia uma mensagem do usuário final.</summary>
    [HttpPost("sessions/{sessionId:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid sessionId, [FromBody] AgentSendTicketChatMessageRequest request, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var ownership = await ResolveOwnedSessionAsync(sessionId, agentId, ct);
        if (ownership.Error is not null)
            return ownership.Error;

        var result = await mediator.Send(new SendTicketChatMessageCommand(
            sessionId,
            AuthorUserId: null,
            AuthorName: request?.AuthorName,
            AuthorKind: "enduser",
            Body: request?.Body ?? string.Empty,
            AttachmentIds: request?.AttachmentIds,
            ClientMessageId: request?.ClientMessageId), ct);

        return MapResult(result, Ok);
    }

    /// <summary>Responde ao convite de chat (aceite/recusa).</summary>
    [HttpPost("sessions/{sessionId:guid}/invite-response")]
    public async Task<IActionResult> RespondInvite(Guid sessionId, [FromBody] AgentTicketChatInviteResponse request, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var result = await mediator.Send(new RespondTicketChatInviteCommand(
            sessionId, agentId, request?.Accepted ?? false, request?.Reason), ct);

        return MapResult(result, Ok);
    }

    /// <summary>Encerra a sessão pelo usuário final.</summary>
    [HttpPost("sessions/{sessionId:guid}/end")]
    public async Task<IActionResult> EndSession(Guid sessionId, [FromBody] AgentTicketChatEndRequest? request, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var ownership = await ResolveOwnedSessionAsync(sessionId, agentId, ct);
        if (ownership.Error is not null)
            return ownership.Error;

        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "user_closed" : request!.Reason!;
        var result = await mediator.Send(new EndTicketChatSessionCommand(sessionId, "user", reason), ct);
        return MapResult(result, Ok);
    }

    // ── Anexos ────────────────────────────────────────────────────────────

    /// <summary>Prepara upload de anexo do chat (allowlist/tamanho de TicketAttachmentSettings).</summary>
    [HttpPost("sessions/{sessionId:guid}/attachments/presigned-upload")]
    public async Task<IActionResult> PrepareAttachmentUpload(Guid sessionId, [FromBody] AgentPresignedUploadRequest req, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var ownership = await ResolveOwnedSessionAsync(sessionId, agentId, ct);
        if (ownership.Error is not null)
            return ownership.Error;

        if (RequireActiveSession(ownership.Session) is { } inactive)
            return inactive;

        var scope = await ResolveAttachmentScopeAsync(ownership.Session!, ct);
        if (scope is null)
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
            scope.Value.EntityType, scope.Value.EntityId, scope.Value.ClientId,
            req.FileName, req.ContentType, req.SizeBytes,
            settings.PresignedUploadUrlTtlMinutes, ct);

        return Ok(result);
    }

    /// <summary>Finaliza o upload do anexo do chat.</summary>
    [HttpPost("sessions/{sessionId:guid}/attachments/complete-upload")]
    public async Task<IActionResult> CompleteAttachmentUpload(Guid sessionId, [FromBody] AgentCompleteUploadRequest req, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var ownership = await ResolveOwnedSessionAsync(sessionId, agentId, ct);
        if (ownership.Error is not null)
            return ownership.Error;

        if (RequireActiveSession(ownership.Session) is { } inactive)
            return inactive;

        var scope = await ResolveAttachmentScopeAsync(ownership.Session!, ct);
        if (scope is null)
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
                req.AttachmentId, scope.Value.EntityType, scope.Value.EntityId, scope.Value.ClientId,
                req.FileName, req.ContentType, req.SizeBytes, req.ObjectKey,
                $"agent:{agentId}", ct);

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
    [HttpGet("sessions/{sessionId:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> DownloadAttachment(Guid sessionId, Guid attachmentId, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var ownership = await ResolveOwnedSessionAsync(sessionId, agentId, ct);
        if (ownership.Error is not null)
            return ownership.Error;

        var scope = await ResolveAttachmentScopeAsync(ownership.Session!, ct);
        var attachment = await attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (scope is null
            || attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, scope.Value.EntityType, StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != scope.Value.EntityId)
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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao baixar anexo {AttachmentId} do chat de suporte", attachmentId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar o arquivo." });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<(IActionResult? Error, TicketChatSessionDto? Session)> ResolveOwnedSessionAsync(Guid sessionId, Guid agentId, CancellationToken ct)
    {
        var state = await mediator.Send(new GetTicketChatSessionQuery(sessionId), ct);
        var session = state.IsSuccess ? state.Value?.Session : null;

        if (session is null || session.AgentId != agentId)
        {
            // 404 (e não 403) para não confirmar a existência de sessões alheias.
            return (NotFound(), null);
        }

        return (null, session);
    }

    /// <summary>
    /// Escopo de armazenamento do anexo. Conversa vinculada a chamado usa o
    /// CHAMADO (compatível com o histórico já gravado); conversa avulsa usa a
    /// SESSÃO. Devolve null quando o chamado/sessão não existe mais.
    /// </summary>
    private async Task<(string EntityType, Guid EntityId, Guid ClientId)?> ResolveAttachmentScopeAsync(
        TicketChatSessionDto session,
        CancellationToken ct)
    {
        if (session.TicketId is { } ticketId)
        {
            var ticket = await ticketQueryService.GetTicketByIdAsync(ticketId, ct);
            return ticket is null ? null : (ChatEntityType, ticket.Id, ticket.ClientId);
        }

        var entity = await chatRepository.GetByIdAsync(session.Id, ct);
        return entity is null ? null : (AdHocChatEntityType, entity.Id, entity.ClientId);
    }

    /// <summary>
    /// Anexo só entra em conversa ATIVA: evita arquivos órfãos no storage para
    /// uma sessão já encerrada (a janela já bloqueia; isto é defesa no servidor).
    /// </summary>
    private IActionResult? RequireActiveSession(TicketChatSessionDto? session)
        => string.Equals(session?.Status, "Active", StringComparison.OrdinalIgnoreCase)
            ? null
            : Conflict(new { code = "session_not_active", error = "A conversa não está ativa." });
}

public record AgentSendTicketChatMessageRequest(
    string Body,
    string? AuthorName = null,
    IReadOnlyList<Guid>? AttachmentIds = null,
    Guid? ClientMessageId = null);

public record AgentTicketChatInviteResponse(bool Accepted, string? Reason = null);

public record AgentTicketChatEndRequest(string? Reason = null);

public record AgentPresignedUploadRequest(string FileName, string ContentType, long SizeBytes);

public record AgentCompleteUploadRequest(
    Guid AttachmentId,
    string ObjectKey,
    string FileName,
    string ContentType,
    long SizeBytes);
