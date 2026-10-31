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
    IChatUserNameResolver userNameResolver,
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

        // Uma sessão por chamado: se já existe PENDENTE/ATIVA, devolve a atual
        // (o atendente reabre a mesma conversa em vez de criar uma paralela).
        //
        // Só uma sessão VIVA é reaproveitada: devolver uma Declined/Expired/Ended
        // fazia o botão "Conversar" do console reabrir uma conversa morta — o
        // usuário recusava, o atendente clicava de novo e recebia 200 com a
        // sessão já recusada (o painel seguia em "Aguardando confirmação").
        // GetCurrentForTicketAsync já filtra Pending/Active.
        var currentForTicket = await chatRepository.GetCurrentForTicketAsync(cmd.TicketId, ct);
        if (currentForTicket is not null)
            return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(currentForTicket));

        // Uma sessão por máquina: evita duas janelas concorrentes no mesmo PC.
        var currentForAgent = await chatRepository.GetCurrentForAgentAsync(agent.Id, ct);
        if (currentForAgent is not null)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("agent_busy"));

        var now = DateTime.UtcNow;
        // O claim do JWT carrega o LOGIN ("pedrostefanogv"), nao o nome. O
        // usuario final precisa ver o nome do tecnico ("Pedro Stefano") na
        // janela do chat — resolve FullName do cadastro, caindo para o que o
        // controller mandou (username) se o cadastro nao tiver nome.
        var startedByName = Trim(await userNameResolver.ResolveAsync(cmd.StartedByUserId, cmd.StartedByName ?? string.Empty, ct), 255);
        var session = new TicketChatSession
        {
            Id = Guid.NewGuid(),
            TicketId = cmd.TicketId,
            AgentId = agent.Id,
            ClientId = ticket.ClientId,
            SiteId = ticket.SiteId,
            Status = TicketChatSessionStatus.Pending,
            StartedByUserId = cmd.StartedByUserId,
            StartedByName = startedByName,
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
            comment: TicketChatAuditText.Started(startedByName));

        await TicketChatInviteDispatcher.DispatchAsync(commandDispatcher, session, ticket.Title, ct);

        // Outro atendente com o mesmo chamado aberto passa a ver a conversa nova
        // sem recarregar a página.
        await envelopePublisher.PublishSessionStatusSignalAsync(session, ct);

        logger.LogInformation(
            "Support chat session {SessionId} created for ticket {TicketId} (agent {AgentId}) by {Support}",
            session.Id, session.TicketId, session.AgentId, startedByName);

        return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// Inicia uma sessão de chat de suporte AVULSA (sem chamado) a partir da
/// máquina escolhida na lista/detalhe do agent no console.
/// </summary>
public sealed class StartAgentSupportChatSessionCommandHandler(
    ITicketChatRepository chatRepository,
    IAgentRepository agentRepository,
    ISiteRepository siteRepository,
    IAgentCommandDispatcher commandDispatcher,
    ITicketChatEnvelopePublisher envelopePublisher,
    IChatUserNameResolver userNameResolver,
    IOptions<TicketChatOptions> options,
    ILogger<StartAgentSupportChatSessionCommandHandler> logger)
    : IRequestHandler<StartAgentSupportChatSessionCommand, Result<TicketChatSessionDto>>
{
    public async Task<Result<TicketChatSessionDto>> Handle(StartAgentSupportChatSessionCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepository.GetByIdAsync(cmd.AgentId);
        if (agent is null || agent.DeletedAt is not null)
            return Result<TicketChatSessionDto>.Failure(Error.NotFound("Agente não encontrado."));

        // Uma sessão por máquina: se já existe PENDENTE/ATIVA, devolve a atual
        // (o atendente reabre a mesma conversa em vez de criar uma paralela).
        // Antes do check de online: a máquina pode ter caído no MEIO da conversa
        // e o atendente ainda precisa retomar o que já existe.
        //
        // Declined/Expired/Ended NÃO são reaproveitadas (GetCurrentForAgentAsync
        // filtra Pending/Active): o "Agora não" do usuário precisa permitir uma
        // NOVA conversa, senão o atendente reabria uma sessão morta.
        var currentForAgent = await chatRepository.GetCurrentForAgentAsync(agent.Id, ct);
        if (currentForAgent is not null)
        {
            // Conversa AVULSA em andamento é privada de quem a iniciou: outro
            // atendente não "entra" nela (nem vê o histórico). Responde o mesmo
            // agent_busy de máquina ocupada, que o console já traduz.
            //
            // Fail-closed também para sessões LEGADAS sem StartedByUserId: são
            // invisíveis em GetCurrentForAgent (IsVisibleToSupportUser exige dono),
            // então reaproveitá-las devolveria "sucesso" para uma tela que nunca
            // carrega a conversa. Mesma regra de visibilidade, um só lugar.
            if (!currentForAgent.IsVisibleToSupportUser(cmd.StartedByUserId))
                return Result<TicketChatSessionDto>.Failure(Error.Conflict("agent_busy"));

            return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(currentForAgent));
        }

        if (agent.Status != AgentStatus.Online)
            return Result<TicketChatSessionDto>.Failure(Error.Conflict("agent_offline"));

        var site = await siteRepository.GetByIdAsync(agent.SiteId);
        var now = DateTime.UtcNow;
        // Mesmo motivo do handler vinculado a chamado: o claim traz o login;
        // o usuario final precisa ver o nome do tecnico.
        var startedByName = Trim(await userNameResolver.ResolveAsync(cmd.StartedByUserId, cmd.StartedByName ?? string.Empty, ct), 255);
        var session = new TicketChatSession
        {
            Id = Guid.NewGuid(),
            TicketId = null,
            AgentId = agent.Id,
            ClientId = site?.ClientId ?? Guid.Empty,
            SiteId = agent.SiteId,
            Status = TicketChatSessionStatus.Pending,
            StartedByUserId = cmd.StartedByUserId,
            StartedByName = startedByName,
            StartedAt = now,
            InviteExpiresAt = now.AddSeconds(Math.Max(30, options.Value.InviteTimeoutSeconds)),
            CreatedAt = now,
            UpdatedAt = now
        };

        await chatRepository.AddSessionAsync(session, ct);

        // Sem chamado não há linha do tempo para auditar: a conversa fica ligada
        // apenas à máquina/tenant. O convite é efeito externo irreversível e vai
        // depois das escritas que podem falhar.
        await TicketChatInviteDispatcher.DispatchAsync(commandDispatcher, session, ticketTitle: string.Empty, ct);

        // Outro atendente com a janela do agent aberta vê a conversa nova sem
        // recarregar a página.
        await envelopePublisher.PublishSessionStatusSignalAsync(session, ct);

        logger.LogInformation(
            "Ad-hoc support chat session {SessionId} created for agent {AgentId} by {Support}",
            session.Id, session.AgentId, startedByName);

        return Result<TicketChatSessionDto>.Success(TicketChatMapper.MapSession(session));
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// Despacha o convite (CommandType.TicketChatInvite) para a máquina da sessão.
/// Compartilhado pelo início vinculado a chamado e pelo início avulso — o
/// contrato do payload fica em um lugar só.
/// </summary>
internal static class TicketChatInviteDispatcher
{
    public static async Task DispatchAsync(
        IAgentCommandDispatcher commandDispatcher,
        TicketChatSession session,
        string? ticketTitle,
        CancellationToken ct)
    {
        if (session.AgentId is null || session.AgentId == Guid.Empty)
            return;

        var payload = TicketChatInvitePayloadFactory.Build(
            session.Id,
            session.TicketId,
            ticketTitle ?? string.Empty,
            session.StartedByName ?? string.Empty,
            session.InviteExpiresAt?.ToString("O"));

        // O comando retornado é capturado de propósito: o AgentCommandDispatcher
        // (Discovery.Api.Services) grava Status=Sent/SentAt quando o publish no
        // NATS dá certo e deixa Pending quando o transporte está fora do ar.
        // É esse estado que decide se o convite entra ou não na reentrega
        // (PendingCommandRedeliveryService) — e a CommandRedeliveryGuard só
        // reentrega convite de sessão ainda Pending e dentro do prazo.
        //
        // A SESSÃO não muda de status aqui: quem a marca como expirada é o job
        // de manutenção (TicketChatMaintenanceJob) e quem a encerra é o usuário.
        var dispatched = await commandDispatcher.DispatchAsync(new AgentCommand
        {
            AgentId = session.AgentId.Value,
            CommandType = CommandType.TicketChatInvite,
            Payload = payload
        }, ct);

        // O dispatcher genérico JÁ grava Status=Sent quando o publish deu certo
        // (e apenas Pending quando o transporte estava caído). Nada a reescrever
        // aqui — só garantir que o contrato não mudou em silêncio: um comando
        // Pending é o único caso em que a reentrega entra em ação.
        _ = dispatched;
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

        var ticket = session.TicketId is { } retryTicketId
            ? await ticketQueryService.GetTicketByIdAsync(retryTicketId, ct)
            : null;
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

        // BUG CORRIGIDO: aqui era um `return Result.Success` silencioso. Quando o
        // usuário clicava "Agora não" e a sessão já tinha saído de Pending (o job
        // de manutenção a marcou Expired, ou o novo clique chegou depois de outra
        // recusa/aceite), o servidor respondia 200 OK sem gravar NADA: o agent
        // logava "reportado ao servidor" e o atendente ficava preso em
        // "Aguardando confirmação" para sempre.
        //
        // Agora a recusa/aceite fora de Pending é CONFLITO explícito
        // (409 + code session_<status>). O agent trata 409 como "já resolvido" e
        // NÃO retenta; o console recebe o estado final pela consulta de sessão.
        if (session.Status != TicketChatSessionStatus.Pending)
        {
            logger.LogInformation(
                "Support chat session {SessionId} invite response {Outcome} ignored: session is already {Status} (agent {AgentId})",
                session.Id, cmd.Accepted ? "accepted" : "declined", session.Status, cmd.AgentId);

            return Result<TicketChatSessionDto>.Failure(
                Error.Conflict($"session_{session.Status.ToString().ToLowerInvariant()}"));
        }

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
            // Recusa também é movimentação do chamado: entra na timeline. Conversa
            // avulsa (sem chamado) não tem timeline.
            if (session.TicketId is { } declinedTicketId)
            {
                await activityLog.LogActivityAsync(
                    declinedTicketId,
                    TicketActivityType.ChatEnded,
                    null,
                    comment: TicketChatAuditText.Declined(session.DeclinedReason));
            }

            // Avisa o AGENT pelo canal durável do chat (o sinal de dashboard
            // abaixo só chega ao CONSOLE). Sem este envelope o agent ficava em
            // "aguardando confirmação" até o próximo poll REST.
            await envelopePublisher.PublishSessionDeclinedAsync(session, ct);
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
            // Recusas causadas pelo AGENT, não pelo usuário:
            //   - "replaced": o convite foi substituído por outro mais recente na
            //     mesma máquina (antes caía em user_denied e o console dizia que o
            //     usuário tinha recusado um convite que ele nunca viu);
            //   - "no_ui": não havia interface na máquina para exibir o pedido de
            //     aceite;
            //   - "user_dismissed": o cartão saiu da tela pela rede de segurança do
            //     agent após o prazo (ver supportChatInviteCardCloseGuard no agent).
            //     Sem o código próprio cairia em user_denied e o console diria que o
            //     usuário recusou — quando ele pode nem ter visto o cartão.
            // Manter os códigos permite o console explicar a causa real.
            "replaced" => "replaced",
            "no_ui" => "no_ui",
            "user_dismissed" => "user_dismissed",
            _ => "user_denied"
        };
}

/// <summary>Envia uma mensagem na sessão (suporte ou usuário final).</summary>
public sealed class SendTicketChatMessageCommandHandler(
    ITicketChatRepository chatRepository,
    ITicketChatEnvelopePublisher envelopePublisher,
    INotificationService notificationService,
    ITicketChatPresenceService presenceService,
    IChatUserNameResolver userNameResolver,
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

        var attachmentIds = cmd.AttachmentIds?.Distinct().ToArray() ?? [];
        if (attachmentIds.Length > options.Value.MaxAttachmentsPerMessage)
            return Result<TicketChatMessageDto>.Failure(Error.Validation("attachmentIds", $"no máximo {options.Value.MaxAttachmentsPerMessage} anexos por mensagem."));

        // BUG CORRIGIDO: mensagem SÓ com anexo (print sem legenda) era recusada
        // como "a mensagem não pode ser vazia". As duas UIs permitem enviar
        // apenas o arquivo, então o print/imagem do suporte nunca saía daqui — o
        // usuário final recebia erro de envio em vez da imagem.
        if (body.Length == 0 && attachmentIds.Length == 0)
            return Result<TicketChatMessageDto>.Failure(Error.Validation("body", "a mensagem não pode ser vazia."));
        if (body.Length > options.Value.MaxMessageLength)
            return Result<TicketChatMessageDto>.Failure(Error.Validation("body", $"a mensagem deve ter no máximo {options.Value.MaxMessageLength} caracteres."));

        if (!TryParseAuthorKind(cmd.AuthorKind, out var authorKind))
            return Result<TicketChatMessageDto>.Failure(Error.Validation("authorKind", "authorKind inválido."));

        var now = DateTime.UtcNow;
        // Mesmo motivo dos handlers de inicio: o claim carrega o login do
        // atendente; resolve o nome de exibicao do cadastro (cacheado) para o
        // usuario final ver "Pedro Stefano" e nao "pedrostefanogv".
        var authorName = authorKind == TicketChatAuthorKind.Support
            ? (await userNameResolver.ResolveAsync(cmd.AuthorUserId, cmd.AuthorName ?? string.Empty, ct)).Trim()
            : (cmd.AuthorName ?? string.Empty).Trim();
        var message = new TicketChatMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            ClientMessageId = cmd.ClientMessageId is { } provided && provided != Guid.Empty ? provided : Guid.NewGuid(),
            AuthorKind = authorKind,
            AuthorUserId = cmd.AuthorUserId,
            AuthorName = string.IsNullOrWhiteSpace(authorName) ? null : authorName,
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

        // Se o atendente está com a JANELA DO CHAT aberta, visível e em foco, ele
        // já está lendo a mensagem na hora: notificar (sino + Web Push do
        // navegador) vira ruído e ainda por cima deixa a caixa cheia de itens que
        // ele acabou de ler. Só avisa quando ele NÃO está vendo a conversa —
        // janela fechada, aba oculta/minimizada ou a janela sem foco.
        // A janela vinculada a chamado registra presença pelo ticket; a janela
        // avulsa (aberta pela lista/detalhe do agent) registra presença pela
        // sessão. Checar as duas cobre os dois fluxos.
        var viewingByTicket = session.TicketId is { } viewingTicketId
            && await presenceService.IsViewingAsync(session.StartedByUserId.Value, viewingTicketId, ct);
        var viewingBySession = await presenceService.IsViewingSessionAsync(session.StartedByUserId.Value, session.Id, ct);

        if (viewingByTicket || viewingBySession)
        {
            logger.LogDebug(
                "Atendente {UserId} está vendo o chat (ticket={TicketId}, session={SessionId}); notificação suprimida",
                session.StartedByUserId, session.TicketId, session.Id);
            return;
        }

        try
        {
            await notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket_chat_message",
                Topic: "ticket-chat",
                // Conversa avulsa não tem chamado: o título não pode mentir.
                Title: session.TicketId is null
                    ? "Nova mensagem no chat de suporte"
                    : "Nova mensagem no chat do chamado",
                Message: Truncate(message.Body, 240),
                Severity: NotificationSeverity.Informational,
                Payload: new { ticketId = session.TicketId, sessionId = session.Id, agentId = session.AgentId, tab = "chat" },
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

        // Auditoria ANTES dos publishes: as escritas que podem falhar ficam antes
        // dos efeitos externos irreversíveis (o agent fechar a janela e o console
        // marcar como encerrado). Se a gravação falhar, a transação reverte sem
        // ter avisado ninguém de um encerramento que não existe.
        if (session.TicketId is { } endedTicketId)
        {
            await activityLog.LogActivityAsync(
                endedTicketId,
                TicketActivityType.ChatEnded,
                cmd.EndedBy == "support" ? session.StartedByUserId : null,
                comment: TicketChatAuditText.Ended(session.EndReason));
        }

        // Mensagem de SISTEMA no histórico: registra QUEM encerrou a conversa,
        // para os dois lados lerem isso na própria thread (o servidor é a fonte
        // do histórico; o console a busca por REST e o agent a recebe no canal).
        var systemMessage = new TicketChatMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            ClientMessageId = Guid.NewGuid(),
            AuthorKind = TicketChatAuthorKind.System,
            Body = TicketChatAuditText.Ended(session.EndReason),
            CreatedAt = DateTime.UtcNow
        };

        var (savedSystemMessage, _) = await chatRepository.AppendMessageAsync(session.Id, systemMessage, [], ct);
        await envelopePublisher.PublishMessageToAgentAsync(session, savedSystemMessage, ct);

        // SessionEnded fecha a janela do agent; o SINAL DE ESTADO é o que avisa o
        // console. Sem ele o operador não via que o usuário encerrou a conversa
        // (a aba do chamado não faz polling).
        await envelopePublisher.PublishSessionEndedAsync(session, ct);
        await envelopePublisher.PublishSessionStatusSignalAsync(session, ct);

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
