using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Cqrs.Tickets.Chat;

/// <summary>
/// Handlers do chat de suporte vinculado ao chamado.
///
/// Regra de ouro do recurso: a SESSÃO só nasce pelo endpoint user-auth
/// (StartTicketChatSessionCommand). Nenhum caminho agent-auth cria sessão.
/// </summary>
public static class TicketChatMapper
{
    public static TicketChatSessionDto MapSession(TicketChatSession session, IReadOnlyList<TicketChatMessageDto>? messages = null)
        => new(
            session.Id,
            session.TicketId,
            session.AgentId,
            session.Status.ToString(),
            session.StartedByUserId,
            session.StartedByName,
            session.StartedAt,
            session.InviteExpiresAt,
            session.AcceptedAt,
            session.DeclinedAt,
            session.DeclinedReason,
            session.EndedAt,
            session.EndedBy?.ToString(),
            session.EndReason,
            session.LastMessageAt,
            session.MessageCount,
            messages);

    public static IReadOnlyList<TicketChatMessageDto> MapMessages(
        IReadOnlyList<TicketChatMessage> messages,
        IReadOnlyList<TicketChatAttachmentRef> attachments)
    {
        var byMessage = attachments
            .GroupBy(a => a.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TicketChatAttachmentDto>)g
                    .Select(a => new TicketChatAttachmentDto(a.AttachmentId, a.FileName, a.ContentType, a.SizeBytes, a.UploadedBy, a.CreatedAt))
                    .ToList());

        return messages
            .Select(m => new TicketChatMessageDto(
                m.Id,
                m.SequenceNumber,
                m.ClientMessageId,
                m.AuthorKind.ToString(),
                m.AuthorUserId,
                m.AuthorName,
                m.Body,
                m.CreatedAt,
                byMessage.TryGetValue(m.Id, out var list) ? list : []))
            .ToList();
    }
}

/// <summary>Inicia a sessão (somente suporte) e despacha o convite ao agent.</summary>
public sealed class StartTicketChatSessionCommandHandler(
    ITicketChatRepository chatRepository,
    ITicketQueryService ticketQueryService,
    IAgentRepository agentRepository,
    IAgentCommandDispatcher commandDispatcher,
    ITicketChatEnvelopePublisher envelopePublisher,
    IActivityLogService activityLog,
    IOptions<TicketChatOptions> options,
    ILogger<StartTicketChatSessionCommandHandler> logger)
    : IRequestHandler<StartTicketChatSessionCommand, Result<TicketChatSessionDto>>
{
    public async Task<Result<TicketChatSessionDto>> Handle(StartTicketChatSessionCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketQueryService.GetTicketByIdAsync(cmd.TicketId, ct);
        if (ticket is null)
            return Result<TicketChatSessionDto>.Failure(Error.NotFound("Chamado não encontrado."));

        if (ticket.AgentId is null || ticket.AgentId == Guid.Empty)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("ticket_has_no_agent"));

        var agent = await agentRepository.GetByIdAsync(ticket.AgentId.Value);
        if (agent is null || agent.DeletedAt is not null)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("ticket_has_no_agent"));

        if (agent.Status != AgentStatus.Online)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("agent_offline"));

        // Uma sessão por chamado: se já existe pendente/ativa, devolve a atual
        // (o atendente reabre a mesma conversa em vez de criar uma paralela).
        var currentForTicket = await chatRepository.GetCurrentForTicketAsync(cmd.TicketId, ct);
        if (currentForTicket is not null)
            return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(currentForTicket));

        // Uma sessão por máquina: evita duas janelas concorrentes no mesmo PC.
        var currentForAgent = await chatRepository.GetCurrentForAgentAsync(agent.Id, ct);
        if (currentForAgent is not null)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("agent_busy"));

        var now = DateTime.UtcNow;
        var session = new TicketChatSession
        {
            Id = Guid.NewGuid(),
            TicketId = cmd.TicketId,
            AgentId = agent.Id,
            ClientId = ticket.ClientId,
            SiteId = ticket.SiteId,
            Status = TicketChatSessionStatus.Pending,
            StartedByUserId = cmd.StartedByUserId,
            StartedByName = Trim(cmd.StartedByName, 255),
            StartedAt = now,
            InviteExpiresAt = now.AddSeconds(Math.Max(30, options.Value.InviteTimeoutSeconds)),
            CreatedAt = now,
            UpdatedAt = now
        };

        await chatRepository.AddSessionAsync(session, ct);

        // Timeline do chamado: cada novo chat precisa aparecer na linha do tempo
        // para o atendente acompanhar a movimentação (quem iniciou e quando).
        //
        // Antes do convite de propósito: o envio ao agent é um efeito externo
        // irreversível; se a auditoria falhar, a transação reverte sem deixar um
        // convite disparado para uma sessão que não existe.
        await activityLog.LogActivityAsync(
            cmd.TicketId,
            TicketActivityType.ChatStarted,
            cmd.StartedByUserId,
            comment: TicketChatAuditText.Started(cmd.StartedByName));

        await DispatchInviteAsync(session, ticket.Title, ct);

        // Outro atendente com o mesmo chamado aberto passa a ver a conversa nova
        // sem recarregar a página.
        await envelopePublisher.PublishSessionStatusSignalAsync(session, ct);

        logger.LogInformation(
            "Support chat session {SessionId} created for ticket {TicketId} (agent {AgentId}) by {Support}",
            session.Id, session.TicketId, session.AgentId, session.StartedByName);

        return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));
    }

    private async Task DispatchInviteAsync(TicketChatSession session, string? ticketTitle, CancellationToken ct)
    {
        var payload = TicketChatInvitePayloadFactory.Build(
            session.Id,
            session.TicketId,
            ticketTitle ?? string.Empty,
            session.StartedByName ?? string.Empty,
            session.InviteExpiresAt?.ToString("O"));

        await commandDispatcher.DispatchAsync(new AgentCommand
        {
            AgentId = session.AgentId!.Value,
            CommandType = CommandType.TicketChatInvite,
            Payload = payload
        }, ct);
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>Reenvia o convite de uma sessão pendente ainda válida.</summary>
public sealed class RetryTicketChatInviteCommandHandler(
    ITicketChatRepository chatRepository,
    ITicketQueryService ticketQueryService,
    IAgentCommandDispatcher commandDispatcher,
    IOptions<TicketChatOptions> options)
    : IRequestHandler<RetryTicketChatInviteCommand, Result<TicketChatSessionDto>>
{
    public async Task<Result<TicketChatSessionDto>> Handle(RetryTicketChatInviteCommand cmd, CancellationToken ct)
    {
        var session = await chatRepository.GetByIdAsync(cmd.SessionId, ct);
        if (session is null)
            return Result<TicketChatSessionDto>.Failure(Error.NotFound("Sessão de chat não encontrada."));

        if (session.Status != TicketChatSessionStatus.Pending)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("session_not_pending"));

        if (session.AgentId is null)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("ticket_has_no_agent"));

        var now = DateTime.UtcNow;
        if (session.InviteExpiresAt is not null && session.InviteExpiresAt <= now)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("invite_expired"));

        var inviteTimeout = TimeSpan.FromSeconds(Math.Max(30, options.Value.InviteTimeoutSeconds));
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, options.Value.InviteRetryCooldownSeconds));
        var lastSentAt = session.InviteExpiresAt is not null ? session.InviteExpiresAt.Value - inviteTimeout : session.StartedAt;
        if (now < lastSentAt + cooldown)
            return Result<TicketChatSessionDto>.Failure(Error.TooManyRequests("invite_cooldown"));

        var ticket = await ticketQueryService.GetTicketByIdAsync(session.TicketId, ct);
        session.InviteExpiresAt = now + inviteTimeout;

        var payload = TicketChatInvitePayloadFactory.Build(
            session.Id,
            session.TicketId,
            ticket?.Title ?? string.Empty,
            session.StartedByName ?? string.Empty,
            session.InviteExpiresAt?.ToString("O"));

        await commandDispatcher.DispatchAsync(new AgentCommand
        {
            AgentId = session.AgentId.Value,
            CommandType = CommandType.TicketChatInvite,
            Payload = payload
        }, ct);

        await chatRepository.UpdateSessionAsync(session, ct);
        return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));
    }
}

/// <summary>Resposta do agent ao convite (aceite/recusa).</summary>
public sealed class RespondTicketChatInviteCommandHandler(
    ITicketChatRepository chatRepository,
    ITicketChatEnvelopePublisher envelopePublisher,
    IActivityLogService activityLog,
    ILogger<RespondTicketChatInviteCommandHandler> logger)
    : IRequestHandler<RespondTicketChatInviteCommand, Result<TicketChatSessionDto>>
{
    public async Task<Result<TicketChatSessionDto>> Handle(RespondTicketChatInviteCommand cmd, CancellationToken ct)
    {
        var session = await chatRepository.GetByIdAsync(cmd.SessionId, ct);

        // Sessão de outro agent responde 404 (sem enumeração).
        if (session is null || session.AgentId != cmd.AgentId)
            return Result<TicketChatSessionDto>.Failure(Error.NotFound("Sessão de chat não encontrada."));

        if (session.Status != TicketChatSessionStatus.Pending)
            return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));

        var now = DateTime.UtcNow;
        if (cmd.Accepted)
        {
            session.Status = TicketChatSessionStatus.Active;
            session.AcceptedAt = now;
        }
        else
        {
            session.Status = TicketChatSessionStatus.Declined;
            session.DeclinedAt = now;
            session.DeclinedReason = NormalizeDeclineReason(cmd.Reason);
        }

        await chatRepository.UpdateSessionAsync(session, ct);

        if (cmd.Accepted)
        {
            await envelopePublisher.PublishSessionOpenedAsync(session, ct);
        }
        else
        {
            // Recusa também é movimentação do chamado: entra na timeline.
            await activityLog.LogActivityAsync(
                session.TicketId,
                TicketActivityType.ChatEnded,
                null,
                comment: TicketChatAuditText.Declined(session.DeclinedReason));
        }

        // O console recebe a mudança de estado (aceite/recusa) imediatamente,
        // sem polling, pelo canal de dashboard (sem corpo de mensagem).
        await envelopePublisher.PublishSessionStatusSignalAsync(session, ct);

        logger.LogInformation(
            "Support chat session {SessionId} invite {Outcome} by agent {AgentId} (reason={Reason})",
            session.Id, cmd.Accepted ? "accepted" : "declined", cmd.AgentId, cmd.Reason);

        return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));
    }

    private static string NormalizeDeclineReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "user_denied" : reason.Trim().ToLowerInvariant() switch
        {
            "timeout" => "timeout",
            "busy" => "busy",
            "offline" => "offline",
            _ => "user_denied"
        };
}

/// <summary>Envia uma mensagem na sessão (suporte ou usuário final).</summary>
public sealed class SendTicketChatMessageCommandHandler(
    ITicketChatRepository chatRepository,
    ITicketChatEnvelopePublisher envelopePublisher,
    INotificationService notificationService,
    IOptions<TicketChatOptions> options,
    ILogger<SendTicketChatMessageCommandHandler> logger)
    : IRequestHandler<SendTicketChatMessageCommand, Result<TicketChatMessageDto>>
{
    public async Task<Result<TicketChatMessageDto>> Handle(SendTicketChatMessageCommand cmd, CancellationToken ct)
    {
        var session = await chatRepository.GetByIdAsync(cmd.SessionId, ct);
        if (session is null)
            return Result<TicketChatMessageDto>.Failure(Error.NotFound("Sessão de chat não encontrada."));

        if (session.Status != TicketChatSessionStatus.Active)
            return Result<TicketChatMessageDto>.Failure(Error.Conflict("session_not_active"));

        var body = (cmd.Body ?? string.Empty).Trim();
        if (body.Length == 0)
            return Result<TicketChatMessageDto>.Failure(Error.Validation("body", "a mensagem não pode ser vazia."));
        if (body.Length > options.Value.MaxMessageLength)
            return Result<TicketChatMessageDto>.Failure(Error.Validation("body", $"a mensagem deve ter no máximo {options.Value.MaxMessageLength} caracteres."));

        var attachmentIds = cmd.AttachmentIds?.Distinct().ToArray() ?? [];
        if (attachmentIds.Length > options.Value.MaxAttachmentsPerMessage)
            return Result<TicketChatMessageDto>.Failure(Error.Validation("attachmentIds", $"no máximo {options.Value.MaxAttachmentsPerMessage} anexos por mensagem."));

        if (!TryParseAuthorKind(cmd.AuthorKind, out var authorKind))
            return Result<TicketChatMessageDto>.Failure(Error.Validation("authorKind", "authorKind inválido."));

        var now = DateTime.UtcNow;
        var message = new TicketChatMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            ClientMessageId = cmd.ClientMessageId is { } provided && provided != Guid.Empty ? provided : Guid.NewGuid(),
            AuthorKind = authorKind,
            AuthorUserId = cmd.AuthorUserId,
            AuthorName = string.IsNullOrWhiteSpace(cmd.AuthorName) ? null : cmd.AuthorName.Trim(),
            Body = body,
            CreatedAt = now
        };

        var (saved, created) = await chatRepository.AppendMessageAsync(session.Id, message, attachmentIds, ct);

        if (created)
        {
            // Sinal SEM corpo para o console em TODA mensagem nova — sem isso um
            // segundo atendente com o chamado aberto só veria a mensagem do
            // colega depois de recarregar a página.
            await envelopePublisher.PublishActivitySignalAsync(session, saved, ct);
        }

        if (created && authorKind == TicketChatAuthorKind.EndUser)
        {
            // Notifica o atendente que iniciou a sessão (sino + Web Push).
            await NotifySupportAsync(session, saved, ct);
        }
        else if (created && authorKind == TicketChatAuthorKind.Support)
        {
            // Mensagem do atendente sai pelo stream durável rumo ao agent.
            await envelopePublisher.PublishMessageToAgentAsync(session, saved, ct);
        }

        var attachments = await chatRepository.GetAttachmentsForMessagesAsync([saved.Id], ct);
        var dto = TicketChatMapper.MapMessages([saved], attachments).First();
        return Result<TicketChatMessageDto>.Success(dto);
    }

    private async Task NotifySupportAsync(TicketChatSession session, TicketChatMessage message, CancellationToken ct)
    {
        if (!options.Value.NotifySupportOnMessage || session.StartedByUserId is null)
            return;

        try
        {
            await notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket_chat_message",
                Topic: "ticket-chat",
                Title: "Nova mensagem no chat do chamado",
                Message: Truncate(message.Body, 240),
                Severity: NotificationSeverity.Informational,
                Payload: new { ticketId = session.TicketId, sessionId = session.Id, tab = "chat" },
                RecipientUserId: session.StartedByUserId), ct);
        }
        catch (Exception ex)
        {
            // Notificação é best-effort: nunca derruba a mensagem já persistida.
            logger.LogWarning(ex, "Falha ao notificar o atendente sobre a mensagem {MessageId}", message.Id);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static bool TryParseAuthorKind(string? raw, out TicketChatAuthorKind kind)
    {
        kind = TicketChatAuthorKind.Support;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "support": kind = TicketChatAuthorKind.Support; return true;
            case "enduser": case "end_user": kind = TicketChatAuthorKind.EndUser; return true;
            case "system": kind = TicketChatAuthorKind.System; return true;
            default: return false;
        }
    }
}

/// <summary>Encerra a sessão (suporte, usuário final ou sistema).</summary>
public sealed class EndTicketChatSessionCommandHandler(
    ITicketChatRepository chatRepository,
    ITicketChatEnvelopePublisher envelopePublisher,
    IActivityLogService activityLog,
    ILogger<EndTicketChatSessionCommandHandler> logger)
    : IRequestHandler<EndTicketChatSessionCommand, Result<TicketChatSessionDto>>
{
    public async Task<Result<TicketChatSessionDto>> Handle(EndTicketChatSessionCommand cmd, CancellationToken ct)
    {
        var session = await chatRepository.GetByIdAsync(cmd.SessionId, ct);
        if (session is null)
            return Result<TicketChatSessionDto>.Failure(Error.NotFound("Sessão de chat não encontrada."));

        if (session.Status is TicketChatSessionStatus.Ended or TicketChatSessionStatus.Declined or TicketChatSessionStatus.Expired)
            return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));

        session.Status = TicketChatSessionStatus.Ended;
        session.EndedAt = DateTime.UtcNow;
        session.EndedBy = ParseEndedBy(cmd.EndedBy);
        session.EndReason = string.IsNullOrWhiteSpace(cmd.Reason) ? "closed" : cmd.Reason.Trim().ToLowerInvariant();

        await chatRepository.UpdateSessionAsync(session, ct);

        // SessionEnded fecha a janela do agent; o SINAL DE ESTADO é o que avisa o
        // console. Sem ele o operador não via que o usuário encerrou a conversa
        // (a aba do chamado não faz polling).
        await envelopePublisher.PublishSessionEndedAsync(session, ct);
        await envelopePublisher.PublishSessionStatusSignalAsync(session, ct);

        await activityLog.LogActivityAsync(
            session.TicketId,
            TicketActivityType.ChatEnded,
            cmd.EndedBy == "support" ? session.StartedByUserId : null,
            comment: TicketChatAuditText.Ended(session.EndReason));

        logger.LogInformation(
            "Support chat session {SessionId} ended (by={EndedBy}, reason={Reason})",
            session.Id, session.EndedBy, session.EndReason);

        return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));
    }

    private static TicketChatEndedBy ParseEndedBy(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "user" => TicketChatEndedBy.User,
            "system" => TicketChatEndedBy.System,
            _ => TicketChatEndedBy.Support
        };
}
