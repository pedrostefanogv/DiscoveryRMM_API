using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação NATS do publicador de envelopes do chat de suporte.
/// </summary>
public class TicketChatEnvelopePublisher(
    IAgentMessaging messaging,
    IAgentRepository agentRepository,
    ISiteRepository siteRepository,
    ILogger<TicketChatEnvelopePublisher> logger) : ITicketChatEnvelopePublisher
{
    /// <summary>Evento canônico do dashboard para atividade de chat (sem corpo).</summary>
    public const string ActivityEventType = "TicketChatActivity";

    public async Task PublishSessionOpenedAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        => await PublishToAgentAsync(session, new TicketChatEnvelope
        {
            EventType = TicketChatEventTypes.SessionOpened,
            SessionId = session.Id,
            TicketId = session.TicketId,
            AgentId = session.AgentId ?? Guid.Empty,
            TimestampUtc = DateTime.UtcNow
        }, cancellationToken);

    public async Task PublishMessageToAgentAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default)
        => await PublishToAgentAsync(session, new TicketChatEnvelope
        {
            EventType = TicketChatEventTypes.Message,
            SessionId = session.Id,
            TicketId = session.TicketId,
            AgentId = session.AgentId ?? Guid.Empty,
            Sequence = message.SequenceNumber,
            AuthorKind = "support",
            AuthorName = message.AuthorName,
            Body = message.Body,
            ClientMessageId = message.ClientMessageId,
            TimestampUtc = message.CreatedAt
        }, cancellationToken);

    public async Task PublishSessionEndedAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        => await PublishToAgentAsync(session, new TicketChatEnvelope
        {
            EventType = TicketChatEventTypes.SessionEnded,
            SessionId = session.Id,
            TicketId = session.TicketId,
            AgentId = session.AgentId ?? Guid.Empty,
            EndReason = session.EndReason,
            TimestampUtc = session.EndedAt ?? DateTime.UtcNow
        }, cancellationToken);

    public async Task PublishActivitySignalAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default)
        => await PublishSignalAsync(session, new
        {
            ticketId = session.TicketId,
            sessionId = session.Id,
            authorKind = message.AuthorKind.ToString().ToLowerInvariant(),
            sequence = message.SequenceNumber
        }, cancellationToken);

    public async Task PublishSessionStatusSignalAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        => await PublishSignalAsync(session, new
        {
            ticketId = session.TicketId,
            sessionId = session.Id,
            authorKind = "system",
            status = session.Status.ToString().ToLowerInvariant()
        }, cancellationToken);

    private async Task PublishToAgentAsync(TicketChatSession session, TicketChatEnvelope envelope, CancellationToken cancellationToken)
    {
        if (session.AgentId is null || session.AgentId == Guid.Empty)
        {
            logger.LogWarning("Support chat session {SessionId} has no agent; envelope {EventType} not published.", session.Id, envelope.EventType);
            return;
        }

        var (clientId, siteId) = await ResolveTenantAsync(session, cancellationToken);
        await messaging.PublishSupportChatServerAsync(clientId, siteId, session.AgentId.Value, envelope, cancellationToken);
    }

    private async Task PublishSignalAsync(TicketChatSession session, object data, CancellationToken cancellationToken)
    {
        var (clientId, siteId) = await ResolveTenantAsync(session, cancellationToken);
        await messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(ActivityEventType, data, clientId, siteId),
            cancellationToken);
    }

    private async Task<(Guid ClientId, Guid SiteId)> ResolveTenantAsync(TicketChatSession session, CancellationToken cancellationToken)
    {
        var clientId = session.ClientId;
        var siteId = session.SiteId ?? Guid.Empty;

        if (siteId == Guid.Empty && session.AgentId is { } agentId && agentId != Guid.Empty)
        {
            var agent = await agentRepository.GetByIdAsync(agentId);
            if (agent is not null)
            {
                siteId = agent.SiteId;
                if (clientId == Guid.Empty)
                {
                    var site = await siteRepository.GetByIdAsync(agent.SiteId);
                    if (site is not null)
                        clientId = site.ClientId;
                }
            }
        }

        return (clientId, siteId);
    }
}
