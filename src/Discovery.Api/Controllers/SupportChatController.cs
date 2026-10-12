using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;
using Discovery.Core.Cqrs.Tickets.Chat.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

/// <summary>
/// Chat de suporte AVULSO (sem chamado) — lado do atendente no console web,
/// aberto direto pela lista/detalhe do agent.
///
/// O fluxo vinculado a chamado continua no <see cref="TicketChatController"/>.
/// Os dois compartilham a mesma sessão/mensagens/anexos e o mesmo stream NATS:
/// o que muda é a porta de entrada e a ausência de chamado (ticket_id nulo).
///
/// Acesso: quando a sessão está vinculada a um chamado (a máquina já estava em
/// chat pelo ticket), a regra de acesso do chamado continua valendo.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/support-chat")]
public class SupportChatController(
    IMediator mediator,
    ITicketChatRepository chatRepository,
    IAgentRepository agentRepository,
    ISiteRepository siteRepository,
    ITicketAccessGuard accessGuard,
    IScopeContext scopeContext,
    ITicketChatPresenceService presenceService,
    IAttachmentService attachmentService,
    IAttachmentRepository attachmentRepository) : ControllerBase
{
    /// <summary>Anexos de conversa vinculada a CHAMADO (compatível com o histórico já gravado).</summary>
    private const string TicketChatEntityType = "TicketChat";

    /// <summary>Anexos de conversa AVULSA (sem chamado), ligados à SESSÃO.</summary>
    private const string AdHocChatEntityType = "SupportChat";

    private string CurrentUserName => User?.Identity?.Name
        ?? (HttpContext.Items["Username"] as string)
        ?? "suporte";

    private Guid? CurrentUserId => HttpContext.Items["UserId"] as Guid?;

    // ── Sessão ────────────────────────────────────────────────────────────

    /// <summary>Sessão pendente/ativa da máquina (HasSession=false quando não há).</summary>
    [HttpGet("agents/{agentId:guid}/sessions/current")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetCurrentForAgent(Guid agentId, CancellationToken ct)
    {
        var session = await chatRepository.GetCurrentForAgentAsync(agentId, ct);
        if (session is null)
            return Ok(new TicketChatStateDto(false, null));

        if (!await CanAccessSessionAsync(session, ActionType.View, ct))
            return Ok(new TicketChatStateDto(false, null));

        var state = await mediator.Send(new GetTicketChatSessionQuery(session.Id), ct);
        return state.ToActionResult();
    }

    /// <summary>Abre a conversa com a máquina (ou devolve a sessão atual da máquina).</summary>
    [HttpPost("agents/{agentId:guid}/sessions")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> StartForAgent(Guid agentId, CancellationToken ct)
    {
        // Escopo: o atendente só abre conversa com máquina permitida no ACL.
        var (clientId, siteId) = await ResolveAgentScopeAsync(agentId, ct);
        if (!await CanAccessAgentScopeAsync(clientId, siteId, ct))
            return NotFound();

        var result = await mediator.Send(
            new StartAgentSupportChatSessionCommand(agentId, CurrentUserId, CurrentUserName), ct);

        if (result.IsFailure)
        {
            var error = result.Errors[0];
            if (string.Equals(error.Code, "Conflict", StringComparison.Ordinal))
                return Conflict(new { code = error.Message, error = DescribeConflict(error.Message) });
        }

        return result.ToActionResult();
    }

    // ── Mensagens ─────────────────────────────────────────────────────────

    /// <summary>Snapshot incremental das mensagens da sessão.</summary>
    [HttpGet("sessions/{sessionId:guid}/messages")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetMessages(
        Guid sessionId,
        [FromQuery] long afterSequence = 0,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (await ResolveSessionAsync(sessionId, ActionType.View, ct) is null)
            return NotFound();

        var result = await mediator.Send(new GetTicketChatMessagesQuery(sessionId, afterSequence, limit), ct);
        return result.ToActionResult();
    }

    /// <summary>Envia uma mensagem do atendente.</summary>
    [HttpPost("sessions/{sessionId:guid}/messages")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SendMessage(
        Guid sessionId,
        [FromBody] SendTicketChatMessageRequest request,
        CancellationToken ct = default)
    {
        if (await ResolveSessionAsync(sessionId, ActionType.Edit, ct) is null)
            return NotFound();

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
    public async Task<IActionResult> EndSession(Guid sessionId, CancellationToken ct)
    {
        if (await ResolveSessionAsync(sessionId, ActionType.Edit, ct) is null)
            return NotFound();

        var result = await mediator.Send(new EndTicketChatSessionCommand(sessionId, "support", "support_closed"), ct);
        return result.ToActionResult();
    }

    /// <summary>Reenvia o convite de uma sessão pendente.</summary>
    [HttpPost("sessions/{sessionId:guid}/invite/retry")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> RetryInvite(Guid sessionId, CancellationToken ct)
    {
        if (await ResolveSessionAsync(sessionId, ActionType.Edit, ct) is null)
            return NotFound();

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

    // ── Presença (janela do chat aberta) ──────────────────────────────────

    /// <summary>Heartbeat: o atendente está com a janela desta conversa aberta e em foco.</summary>
    [HttpPost("sessions/{sessionId:guid}/presence")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> TouchPresence(Guid sessionId, [FromQuery] string? clientId, CancellationToken ct)
    {
        if (CurrentUserId is not { } userId)
            return Unauthorized();

        if (await ResolveSessionAsync(sessionId, ActionType.View, ct) is null)
            return NotFound();

        await presenceService.TouchSessionAsync(userId, sessionId, clientId, ct);
        return NoContent();
    }

    /// <summary>Limpa a presença (janela fechada, aba oculta ou ao desmontar a tela).</summary>
    [HttpDelete("sessions/{sessionId:guid}/presence")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> ClearPresence(Guid sessionId, [FromQuery] string? clientId, CancellationToken ct)
    {
        if (CurrentUserId is { } userId)
            await presenceService.ClearSessionAsync(userId, sessionId, clientId, ct);

        return NoContent();
    }

    // ── Anexos ────────────────────────────────────────────────────────────

    /// <summary>Prepara o upload direto no object storage para um anexo do chat.</summary>
    [HttpPost("sessions/{sessionId:guid}/attachments/presigned-upload")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> PrepareAttachmentUpload(
        Guid sessionId,
        [FromBody] PresignedUploadRequestDto req,
        CancellationToken ct)
    {
        var session = await ResolveSessionAsync(sessionId, ActionType.Edit, ct);
        if (session is null)
            return NotFound();

        if (RequireActiveSession(session) is { } inactive)
            return inactive;

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

        var scope = ResolveAttachmentScope(session);
        var result = await attachmentService.PreparePresignedUploadAsync(
            scope.EntityType, scope.EntityId, scope.ClientId, req.FileName, req.ContentType, req.SizeBytes,
            settings.PresignedUploadUrlTtlMinutes, ct);

        return Ok(result);
    }

    /// <summary>Finaliza o upload pré-assinado e persiste o anexo do chat.</summary>
    [HttpPost("sessions/{sessionId:guid}/attachments/complete-upload")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CompleteAttachmentUpload(
        Guid sessionId,
        [FromBody] CompleteUploadRequestDto req,
        CancellationToken ct)
    {
        var session = await ResolveSessionAsync(sessionId, ActionType.Edit, ct);
        if (session is null)
            return NotFound();

        if (RequireActiveSession(session) is { } inactive)
            return inactive;

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
            var scope = ResolveAttachmentScope(session);
            var attachment = await attachmentService.CompletePresignedUploadAsync(
                req.AttachmentId, scope.EntityType, scope.EntityId, scope.ClientId,
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

    /// <summary>Baixa um anexo do chat (streaming same-origin).</summary>
    [HttpGet("sessions/{sessionId:guid}/attachments/{attachmentId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> DownloadAttachment(Guid sessionId, Guid attachmentId, CancellationToken ct)
    {
        var session = await ResolveSessionAsync(sessionId, ActionType.View, ct);
        if (session is null)
            return NotFound();

        var scope = ResolveAttachmentScope(session);
        var attachment = await attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, scope.EntityType, StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != scope.EntityId)
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
    /// Devolve a sessão validando o acesso do usuário. Sessão vinculada a
    /// chamado exige acesso ao chamado (mesma regra do console de chamados);
    /// sessão avulsa exige apenas a permissão de chamados já aplicada pela rota.
    /// </summary>
    private async Task<TicketChatSession?> ResolveSessionAsync(Guid sessionId, ActionType action, CancellationToken ct)
    {
        var session = await chatRepository.GetByIdAsync(sessionId, ct);
        if (session is null)
            return null;

        return await CanAccessSessionAsync(session, action, ct) ? session : null;
    }

    /// <summary>
    /// Acesso à sessão: vinculada a chamado usa a regra do chamado; avulsa usa o
    /// escopo do cliente dono da máquina (mesma regra das listagens de agentes).
    /// </summary>
    private async Task<bool> CanAccessSessionAsync(TicketChatSession session, ActionType action, CancellationToken ct)
    {
        if (session.TicketId is { } ticketId)
            return await accessGuard.CanAccessTicketAsync(ticketId, action, ct);

        return await CanAccessAgentScopeAsync(session.ClientId, session.SiteId ?? Guid.Empty, ct);
    }

    private async Task<(Guid ClientId, Guid SiteId)> ResolveAgentScopeAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await agentRepository.GetByIdAsync(agentId);
        if (agent is null)
            return (Guid.Empty, Guid.Empty);

        var site = await siteRepository.GetByIdAsync(agent.SiteId);
        return (site?.ClientId ?? Guid.Empty, agent.SiteId);
    }

    /// <summary>
    /// Acesso à MÁQUINA: mesma regra do <see cref="ITicketAccessGuard"/> para
    /// chamados — global passa direto; os demais precisam do cliente OU do site
    /// no ACL (usuário com escopo apenas por site, senão, seria bloqueado).
    /// </summary>
    private async Task<bool> CanAccessAgentScopeAsync(Guid clientId, Guid siteId, CancellationToken ct)
    {
        if (clientId == Guid.Empty && siteId == Guid.Empty)
            return false;

        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (access.HasGlobalAccess)
            return true;

        return (clientId != Guid.Empty && access.AllowedClientIds.Contains(clientId))
            || (siteId != Guid.Empty && access.AllowedSiteIds.Contains(siteId));
    }

    /// <summary>Escopo de armazenamento do anexo, igual ao do lado agent-auth.</summary>
    private static (string EntityType, Guid EntityId, Guid ClientId) ResolveAttachmentScope(TicketChatSession session)
        => session.TicketId is { } ticketId
            ? (TicketChatEntityType, ticketId, session.ClientId)
            : (AdHocChatEntityType, session.Id, session.ClientId);

    /// <summary>
    /// Anexo só entra em conversa ATIVA: evita arquivos órfãos no storage para
    /// uma sessão que já terminou (a UI já bloqueia, isto é defesa no servidor).
    /// </summary>
    private IActionResult? RequireActiveSession(TicketChatSession session)
        => session.Status == TicketChatSessionStatus.Active
            ? null
            : Conflict(new { code = "session_not_active", error = "A conversa não está ativa." });

    private static string DescribeConflict(string code) => code switch
    {
        "ticket_has_no_agent" => "O chamado não está vinculado a uma máquina.",
        "agent_offline" => "A máquina está offline; não é possível abrir o chat agora.",
        "agent_busy" => "A máquina já está em outro chat de suporte.",
        "session_not_pending" => "Esta conversa não está mais aguardando confirmação.",
        "invite_expired" => "O convite expirou. Inicie uma nova conversa.",
        "invite_cooldown" => "Aguarde alguns segundos antes de reenviar o convite.",
        "session_not_active" => "A conversa não está ativa.",
        _ => "Não foi possível concluir a operação."
    };
}
