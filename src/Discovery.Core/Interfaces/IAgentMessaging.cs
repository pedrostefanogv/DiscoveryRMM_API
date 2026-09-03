namespace Discovery.Core.Interfaces;

using Discovery.Core.DTOs;

/// <summary>
/// Abstração para envio de mensagens em tempo real para agents.
/// Implementação: NATS. Preparado para troca de transport.
/// </summary>
public interface IAgentMessaging
{
    /// <summary>Envia um comando para um agent específico.</summary>
    Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload);

    /// <summary>
    /// Publica comando em massa no subject de site (tenant.{c}.site.{s}.agents.command).
    /// </summary>
    Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publica comando em massa no subject de cliente (tenant.{c}.agents.command).
    /// </summary>
    Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publica comando em massa no subject global (tenant.global.agents.command).
    /// </summary>
    Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>Publica evento para o dashboard (broadcast).</summary>
    Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default);

    /// <summary>Envia um ping leve de invalidacao de sync para um agent especifico.</summary>
    Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publica sync ping com subject explicito (override de clientId/siteId), sem resolver
    /// do banco. Necessário em janelas de transição (ex: transferência de site), quando o
    /// agent ainda está subscrito nos subjects do site antigo.
    /// </summary>
    Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envia um comando para um subject de agent explicito (sem resolver site/cliente do
    /// banco). Usado em janelas de transição para alcançar o agent pelo subject antigo.
    /// </summary>
    Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload);

    /// <summary>Registra handler para mensagens de agents (heartbeat, command result, hardware report).</summary>
    Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken);

    /// <summary>Verifica se o serviço de mensageria está conectado.</summary>
    bool IsConnected { get; }
}
