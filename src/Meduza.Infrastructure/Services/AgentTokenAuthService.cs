using System.Security.Cryptography;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Autenticação baseada em token para agents.
/// Gera tokens aleatórios, armazena hash SHA-256 no banco.
/// Preparado para ser substituído/complementado por mTLS.
/// </summary>
public class AgentTokenAuthService : IAgentAuthService
{
    private readonly IAgentTokenRepository _tokenRepo;
    private readonly IConfigurationResolver _configResolver;
    private readonly IAgentRepository _agentRepo;

    public AgentTokenAuthService(
        IAgentTokenRepository tokenRepo,
        IConfigurationResolver configResolver,
        IAgentRepository agentRepo)
    {
        _tokenRepo = tokenRepo;
        _configResolver = configResolver;
        _agentRepo = agentRepo;
    }

    public async Task<(AgentToken Token, string RawToken)> CreateTokenAsync(Guid agentId, string? description, int? expirationDays = null)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found");

        // Se não passou expirationDays, resolver via hierarquia de configuração
        if (expirationDays is null)
        {
            var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
            expirationDays = resolved.TokenExpirationDays;
        }

        var rawToken = GenerateRawToken();
        var tokenHash = HashToken(rawToken);
        var prefix = rawToken[..8];

        var token = new AgentToken
        {
            Id = IdGenerator.NewId(),
            AgentId = agentId,
            TokenHash = tokenHash,
            TokenPrefix = prefix,
            Description = description,
            ExpiresAt = DateTime.UtcNow.AddDays(expirationDays.Value),
            CreatedAt = DateTime.UtcNow
        };

        await _tokenRepo.CreateAsync(token);
        return (token, rawToken);
    }

    public async Task<AgentToken?> ValidateTokenAsync(string rawToken)
    {
        var hash = HashToken(rawToken);
        var token = await _tokenRepo.GetByTokenHashAsync(hash);

        if (token is null || !token.IsValid)
            return null;

        await _tokenRepo.UpdateLastUsedAsync(token.Id);
        return token;
    }

    public async Task RevokeTokenAsync(Guid tokenId)
    {
        await _tokenRepo.RevokeAsync(tokenId);
    }

    public async Task RevokeAllTokensAsync(Guid agentId)
    {
        await _tokenRepo.RevokeAllByAgentIdAsync(agentId);
    }

    public async Task<IEnumerable<AgentToken>> GetTokensByAgentIdAsync(Guid agentId)
    {
        return await _tokenRepo.GetByAgentIdAsync(agentId);
    }

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"mdz_{Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')}";
    }

    internal static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
