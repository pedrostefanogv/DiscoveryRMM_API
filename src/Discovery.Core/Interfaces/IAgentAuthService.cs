using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Resultado da validação de token de agent, carregando o estado de soft-delete
/// do agent obtido na mesma consulta ao repositório para evitar SQL duplicado no middleware.
/// </summary>
public sealed record AgentTokenValidationResult(AgentToken Token, bool? IsAgentTrashed = null);

/// <summary>
/// Abstração de autenticação de agents. 
/// Implementação atual: token-based. Preparado para mTLS no futuro.
/// </summary>
public interface IAgentAuthService
{
    Task<(AgentToken Token, string RawToken)> CreateTokenAsync(Guid agentId, string? description);
    Task<AgentToken?> ValidateTokenAsync(string rawToken);

    /// <summary>
    /// Valida o token e retorna também se o agent associado está na lixeira
    /// (<see cref="AgentTokenValidationResult.IsAgentTrashed"/>), evitando uma segunda
    /// leitura de <see cref="IAgentRepository.GetByIdForDecommissionAsync"/> no middleware.
    /// Implementações legadas que retornam <c>null</c> em <c>IsAgentTrashed</c>
    /// acionam o fallback no middleware.
    /// </summary>
    async Task<AgentTokenValidationResult?> ValidateTokenWithStatusAsync(string rawToken)
    {
        var token = await ValidateTokenAsync(rawToken);
        return token is null ? null : new AgentTokenValidationResult(token, IsAgentTrashed: null);
    }
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
