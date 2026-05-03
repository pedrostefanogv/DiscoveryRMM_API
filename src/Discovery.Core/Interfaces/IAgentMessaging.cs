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

    /// <summary>Publica evento para o dashboard (broadcast).</summary>
    Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default);

    /// <summary>Envia um ping leve de invalidacao de sync para um agent especifico.</summary>
    Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default);

    /// <summary>Registra handler para mensagens de agents (heartbeat, command result, hardware report).</summary>
    Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publica snapshot de descoberta P2P no subject do site.
    /// Apenas o servidor publica; agents assinam o subject do próprio site.
    /// </summary>
    Task PublishP2pDiscoverySnapshotAsync(Guid clientId, Guid siteId, string payload, CancellationToken cancellationToken = default);

    /// <summary>Verifica se o serviço de mensageria está conectado.</summary>
    bool IsConnected { get; }
}
