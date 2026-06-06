using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Abstração de autenticação de agents. 
/// Implementação atual: token-based. Preparado para mTLS no futuro.
/// </summary>
public interface IAgentAuthService
{
    Task<(AgentToken Token, string RawToken)> CreateTokenAsync(Guid agentId, string? description);
    Task<AgentToken?> ValidateTokenAsync(string rawToken);
    Task RevokeTokenAsync(Guid tokenId);
    Task RevokeAllTokensAsync(Guid agentId);
    Task<IEnumerable<AgentToken>> GetTokensByAgentIdAsync(Guid agentId);

    /// <summary>
    /// Tenta adquirir uma sessão NATS exclusiva para este token.
    /// Retorna true se a sessão foi adquirida (token não está em uso por outra conexão).
    /// Usa Redis SET NX com TTL como trava distribuída.
    /// </summary>
    Task<bool> TryAcquireNatsSessionAsync(Guid tokenId, Guid agentId, string userNkey, TimeSpan sessionTtl);

    /// <summary>
    /// Libera a sessão NATS associada a este token (ex: na desconexão).
    /// </summary>
    Task ReleaseNatsSessionAsync(Guid tokenId);

    /// <summary>
    /// Atualiza o timestamp de última conexão NATS bem-sucedida no banco.
    /// </summary>
    Task UpdateLastNatsConnectedAsync(Guid tokenId);
}
