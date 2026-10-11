using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Ingestão dos envelopes do chat de suporte publicados pelo agent
/// (agent -> servidor) no stream JetStream DISCOVERY_SUPPORT_CHAT.
///
/// Implementado fora do NatsAgentMessaging para evitar dependência circular
/// (o serviço de domínio precisa publicar de volta no canal servidor -> agent).
/// </summary>
public interface ITicketChatIngestService
{
    /// <summary>
    /// Processa um envelope publicado pelo agent. Retorna false quando o
    /// envelope foi descartado por contrato/idempotência (já é logado).
    /// </summary>
    Task<bool> HandleAgentEnvelopeAsync(TicketChatEnvelope envelope, Guid agentId, CancellationToken cancellationToken = default);
}
