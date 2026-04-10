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
}
