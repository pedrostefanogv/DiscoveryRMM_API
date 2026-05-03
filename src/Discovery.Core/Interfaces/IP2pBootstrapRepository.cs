using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório para o sistema de bootstrap P2P via cloud.
/// Gerencia o registro de presença dos agents e a descoberta de peers.
/// </summary>
public interface IP2pBootstrapRepository
{
    /// <summary>
    /// Insere ou atualiza o registro de bootstrap de um agent (upsert por AgentId).
    /// </summary>
    Task UpsertAsync(AgentP2pBootstrap bootstrap);

    /// <summary>
    /// Retorna até <paramref name="count"/> peers aleatórios do mesmo cliente,
    /// excluindo o próprio agent, considerando apenas agents com LastSeenAt >= onlineCutoff.
    /// </summary>
    /// <param name="clientId">ID do cliente — peers são restritos ao mesmo cliente.</param>
    /// <param name="excludeAgentId">ID do agent solicitante (excluído da lista).</param>
    /// <param name="count">Número máximo de peers a retornar (padrão: 3).</param>
    /// <param name="onlineCutoff">Agents com LastSeenAt anterior a este timestamp são considerados offline.</param>
    Task<IReadOnlyList<AgentP2pBootstrap>> GetRandomPeersAsync(
        Guid clientId,
        Guid excludeAgentId,
        int count,
        DateTime onlineCutoff);

    /// <summary>
    /// Retorna todos os peers ativos de um site para montar snapshot de descoberta.
    /// Inclui apenas agents com bootstrap registrado e LastSeenAt >= onlineCutoff.
    /// </summary>
    Task<IReadOnlyList<AgentP2pBootstrap>> GetSitePeersAsync(
        Guid siteId,
        DateTime onlineCutoff,
        int maxPeers);
}
