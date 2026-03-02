namespace Meduza.Core.Interfaces;

/// <summary>
/// Abstração para envio de mensagens em tempo real para agents.
/// Implementação: NATS. Preparado para troca de transport.
/// </summary>
public interface IAgentMessaging
{
    /// <summary>Envia um comando para um agent específico.</summary>
    Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload);

    /// <summary>Publica evento para o dashboard (broadcast).</summary>
    Task PublishDashboardEventAsync(string eventType, object data);

    /// <summary>Registra handler para mensagens de agents (heartbeat, command result, hardware report).</summary>
    Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken);

    /// <summary>Verifica se o serviço de mensageria está conectado.</summary>
    bool IsConnected { get; }
}
