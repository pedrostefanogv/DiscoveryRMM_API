using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Ingestão dos envelopes do chat de suporte publicados pelo agent no stream
/// JetStream (tenant.*.site.*.agent.*.supportchat.agent).
///
/// Não persiste nada diretamente: delega para os mesmos commands usados pelos
/// endpoints REST, garantindo UM caminho de validação/idempotência/idempotência
/// de sinalização para as duas origens (NATS e REST de fallback).
/// </summary>
public class TicketChatIngestService(
    ITicketChatRepository chatRepository,
    IMediator mediator,
    ILogger<TicketChatIngestService> logger) : ITicketChatIngestService
{
    public async Task<bool> HandleAgentEnvelopeAsync(
        TicketChatEnvelope envelope,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        var session = await chatRepository.GetByIdAsync(envelope.SessionId, cancellationToken);

        // Sessão inexistente ou de OUTRO agent: descarta (sem enumeração e sem
        // permitir que uma máquina injete mensagens em chat alheio).
        if (session is null || session.AgentId != agentId)
        {
            logger.LogWarning(
                "[CONTRACT_VIOLATION] component=Server field=SupportChatSession reason=SessionNotOwnedByAgent session={SessionId} agent={AgentId} subjectEvent={EventType}",
                envelope.SessionId, agentId, envelope.EventType);
            return false;
        }

        switch (envelope.EventType)
        {
            case TicketChatEventTypes.Message:
            {
                var result = await mediator.Send(new SendTicketChatMessageCommand(
                    session.Id,
                    AuthorUserId: null,
                    AuthorName: envelope.AuthorName,
                    AuthorKind: "enduser",
                    Body: envelope.Body ?? string.Empty,
                    AttachmentIds: envelope.AttachmentIds,
                    ClientMessageId: envelope.ClientMessageId == Guid.Empty ? null : envelope.ClientMessageId), cancellationToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "Support chat: mensagem do agent rejeitada (session={SessionId}, erros={Errors})",
                        session.Id, string.Join(';', result.Errors.Select(e => e.Code)));
                }

                return result.IsSuccess;
            }

            case TicketChatEventTypes.InviteResponse:
            {
                var result = await mediator.Send(new RespondTicketChatInviteCommand(
                    session.Id,
                    agentId,
                    envelope.Accepted ?? false,
                    envelope.Reason), cancellationToken);

                return result.IsSuccess;
            }

            case TicketChatEventTypes.SessionEnded:
            {
                var result = await mediator.Send(new EndTicketChatSessionCommand(
                    session.Id,
                    EndedBy: "user",
                    Reason: string.IsNullOrWhiteSpace(envelope.EndReason) ? "user_closed" : envelope.EndReason), cancellationToken);

                return result.IsSuccess;
            }

            default:
                logger.LogWarning(
                    "[CONTRACT_VIOLATION] component=Server field=SupportChatEnvelope reason=UnknownEventType eventType={EventType} session={SessionId}",
                    envelope.EventType, envelope.SessionId);
                return false;
        }
    }
}
