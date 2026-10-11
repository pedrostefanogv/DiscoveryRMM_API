using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Publica os envelopes do chat de suporte no NATS (stream JetStream
/// DISCOVERY_SUPPORT_CHAT) e o sinal de atividade no canal de dashboard.
///
/// O corpo das mensagens NUNCA vai para o dashboard: o console recebe apenas
/// `{ticketId, sessionId, authorKind, sequence}` e busca o conteúdo por REST.
/// </summary>
public interface ITicketChatEnvelopePublisher
{
    /// <summary>Servidor -> agent: sessão aberta (usuário aceitou o convite).</summary>
    Task PublishSessionOpenedAsync(TicketChatSession session, CancellationToken cancellationToken = default);

    /// <summary>Servidor -> agent: mensagem do atendente.</summary>
    Task PublishMessageToAgentAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>Servidor -> agent: sessão encerrada.</summary>
    Task PublishSessionEndedAsync(TicketChatSession session, CancellationToken cancellationToken = default);

    /// <summary>Servidor -> console: sinal de nova mensagem (sem corpo).</summary>
    Task PublishActivitySignalAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>Servidor -> console: mudança de estado da sessão (aceite/recusa/encerramento).</summary>
    Task PublishSessionStatusSignalAsync(TicketChatSession session, CancellationToken cancellationToken = default);
}
