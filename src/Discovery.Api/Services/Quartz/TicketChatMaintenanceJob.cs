using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Manutenção do chat de suporte vinculado ao chamado:
/// - expira convites pendentes vencidos (`InviteTimeoutSeconds`);
/// - encerra sessões ativas sem atividade (`InactivityTimeoutMinutes`);
/// - encerra sessões ativas de chamados já fechados.
///
/// Em todos os casos publica o sinal de estado no canal de dashboard (sem
/// corpo) para o console refletir a mudança sem polling.
/// Agenda: a cada 1 minuto.
/// </summary>
[DisallowConcurrentExecution]
public sealed class TicketChatMaintenanceJob : IJob
{
    public static readonly JobKey Key = new("ticket-chat-maintenance", "maintenance");

    private const int BatchSize = 200;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<TicketChatMaintenanceJob>();

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITicketChatRepository>();
        var publisher = scope.ServiceProvider.GetRequiredService<ITicketChatEnvelopePublisher>();
        var activityLog = scope.ServiceProvider.GetRequiredService<IActivityLogService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<TicketChatOptions>>().Value;
        var ct = context.CancellationToken;

        var now = DateTime.UtcNow;

        var expiredCount = await ExpirePendingInvitesAsync(repository, publisher, activityLog, now, logger, ct);

        var inactivityCutoff = now.AddMinutes(-Math.Max(1, options.InactivityTimeoutMinutes));
        var inactivityCount = await CloseInactiveSessionsAsync(repository, publisher, activityLog, inactivityCutoff, logger, ct);

        var ticketClosedCount = await CloseSessionsOfClosedTicketsAsync(repository, publisher, activityLog, logger, ct);

        logger.LogInformation(
            "Ticket chat maintenance: expired invites={Expired}, inactive sessions={Inactive}, ticket-closed sessions={TicketClosed}",
            expiredCount, inactivityCount, ticketClosedCount);

        context.Result = new
        {
            expiredInvites = expiredCount,
            inactiveSessions = inactivityCount,
            ticketClosedSessions = ticketClosedCount
        };
    }

    /// <summary>
    /// Grava no histórico a mensagem de SISTEMA que registra quem (ou o que)
    /// encerrou a conversa, e entrega ao agent para ele exibir na thread.
    /// </summary>
    private static async Task AppendClosureMessageAsync(
        ITicketChatRepository repository,
        ITicketChatEnvelopePublisher publisher,
        TicketChatSession session,
        CancellationToken ct)
    {
        var message = new TicketChatMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            ClientMessageId = Guid.NewGuid(),
            AuthorKind = TicketChatAuthorKind.System,
            Body = TicketChatAuditText.Ended(session.EndReason),
            CreatedAt = DateTime.UtcNow
        };

        var (saved, _) = await repository.AppendMessageAsync(session.Id, message, [], ct);
        await publisher.PublishMessageToAgentAsync(session, saved, ct);
    }

    private static async Task<int> ExpirePendingInvitesAsync(
        ITicketChatRepository repository,
        ITicketChatEnvelopePublisher publisher,
        IActivityLogService activityLog,
        DateTime nowUtc,
        ILogger logger,
        CancellationToken ct)
    {
        var pending = await repository.GetExpiredPendingAsync(nowUtc, BatchSize, ct);
        var count = 0;

        foreach (var session in pending)
        {
            session.Status = TicketChatSessionStatus.Expired;
            session.DeclinedAt = nowUtc;
            session.DeclinedReason = "timeout";
            session.EndedAt = nowUtc;
            session.EndedBy = TicketChatEndedBy.System;
            session.EndReason = "invite_timeout";

            await repository.UpdateSessionAsync(session, ct);

            // O agent também precisa saber que o convite morreu: o sinal de
            // dashboard abaixo só chega ao CONSOLE. Sem o envelope durável o
            // agent seguia em "aguardando confirmação" até o próximo poll REST
            // (e a janela de chat dele ficava aberta sem conversa).
            // Vai pelo canal do chat (PublishSupportChatServerAsync), o mesmo
            // dos demais eventos da sessão.
            await publisher.PublishSessionDeclinedAsync(session, ct);
            await publisher.PublishSessionStatusSignalAsync(session, ct);

            // Conversa avulsa (sem chamado) não tem linha do tempo para auditar.
            if (session.TicketId is { } ticketId)
            {
                await activityLog.LogActivityAsync(
                    ticketId,
                    TicketActivityType.ChatEnded,
                    null,
                    comment: TicketChatAuditText.Declined("timeout"));
            }

            count++;
        }

        if (count > 0)
            logger.LogInformation("Ticket chat: {Count} convite(s) expirado(s).", count);

        return count;
    }

    private static async Task<int> CloseInactiveSessionsAsync(
        ITicketChatRepository repository,
        ITicketChatEnvelopePublisher publisher,
        IActivityLogService activityLog,
        DateTime cutoffUtc,
        ILogger logger,
        CancellationToken ct)
    {
        var sessions = await repository.GetInactiveActiveAsync(cutoffUtc, BatchSize, ct);
        var count = 0;

        foreach (var session in sessions)
        {
            session.Status = TicketChatSessionStatus.Ended;
            session.EndedAt = DateTime.UtcNow;
            session.EndedBy = TicketChatEndedBy.System;
            session.EndReason = "inactivity";

            await repository.UpdateSessionAsync(session, ct);
            await AppendClosureMessageAsync(repository, publisher, session, ct);
            await publisher.PublishSessionEndedAsync(session, ct);
            await publisher.PublishSessionStatusSignalAsync(session, ct);

            if (session.TicketId is { } ticketId)
            {
                await activityLog.LogActivityAsync(
                    ticketId,
                    TicketActivityType.ChatEnded,
                    null,
                    comment: TicketChatAuditText.Ended("inactivity"));
            }

            count++;
        }

        if (count > 0)
            logger.LogInformation("Ticket chat: {Count} sessão(ões) encerrada(s) por inatividade.", count);

        return count;
    }

    private static async Task<int> CloseSessionsOfClosedTicketsAsync(
        ITicketChatRepository repository,
        ITicketChatEnvelopePublisher publisher,
        IActivityLogService activityLog,
        ILogger logger,
        CancellationToken ct)
    {
        var sessions = await repository.GetActiveForClosedTicketsAsync(BatchSize, ct);
        var count = 0;

        foreach (var session in sessions)
        {
            session.Status = TicketChatSessionStatus.Ended;
            session.EndedAt = DateTime.UtcNow;
            session.EndedBy = TicketChatEndedBy.System;
            session.EndReason = "ticket_closed";

            await repository.UpdateSessionAsync(session, ct);
            await AppendClosureMessageAsync(repository, publisher, session, ct);
            await publisher.PublishSessionEndedAsync(session, ct);
            await publisher.PublishSessionStatusSignalAsync(session, ct);

            if (session.TicketId is { } ticketId)
            {
                await activityLog.LogActivityAsync(
                    ticketId,
                    TicketActivityType.ChatEnded,
                    null,
                    comment: TicketChatAuditText.Ended("ticket_closed"));
            }

            count++;
        }

        if (count > 0)
            logger.LogInformation("Ticket chat: {Count} sessão(ões) encerrada(s) por fechamento do chamado.", count);

        return count;
    }
}
