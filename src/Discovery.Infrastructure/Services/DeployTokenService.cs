using System.Security.Cryptography;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public class DeployTokenService : IDeployTokenService
{
    /// <summary>
    /// TTL do deploy token one-shot do zero-touch (fluxo P2P de onboarding).
    /// </summary>
    /// <remarks>
    /// Antes era 1 minuto: a oferta assinada entregue ao agente (p2p_onboarding_http.go,
    /// onboardingDeployKeyTTL) anunciaria até 30 min, mas a chave morria no servidor em
    /// 1 min. Como o agente não configurado pode levar minutos entre descobrir o peer
    /// provisionador e concluir o POST /api/v1/agent-register (retries de discovery,
    /// handshake libp2p, jitter do loop de onboarding), o registro falhava com 401/403 e
    /// o agente ficava preso em "aguardando provisionamento".
    /// 10 minutos cobrem a janela real do handshake sem transformar a chave em
    /// credencial de longa duração; MaxUses=1 mantém o uso único (anti-replay).
    /// </remarks>
    private static readonly TimeSpan ZeroTouchTokenTtl = TimeSpan.FromMinutes(10);

    private readonly IDeployTokenRepository _tokenRepo;

    public DeployTokenService(IDeployTokenRepository tokenRepo)
    {
        _tokenRepo = tokenRepo;
    }

    public async Task<(DeployToken Token, string RawToken)> CreateTokenAsync(Guid clientId, Guid siteId, string? description, int? expiresInHours = null, bool multiUse = false)
    {
        var hours = expiresInHours ?? 4;
        int? maxUses = multiUse ? null : 1;

        var rawToken = GenerateRawToken();
        var tokenHash = HashToken(rawToken);
        var prefix = rawToken[..12];

        var token = new DeployToken
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            SiteId = siteId,
            TokenHash = tokenHash,
            TokenPrefix = prefix,
            Description = description,
            ExpiresAt = DateTime.UtcNow.AddHours(hours),
            CreatedAt = DateTime.UtcNow,
            UsedCount = 0,
            MaxUses = maxUses
        };

        await _tokenRepo.CreateAsync(token);
        return (token, rawToken);
    }

    public async Task<DeployToken?> TryUseTokenAsync(string rawToken)
    {
        var hash = HashToken(rawToken);
        var token = await _tokenRepo.TryUseByTokenHashAsync(hash, DateTime.UtcNow);
        if (token is null)
            return null;

        // Bloquear tokens legados sem escopo para evitar instalação fora de cliente/site.
        if (!token.ClientId.HasValue || !token.SiteId.HasValue)
            return null;

        return token;
    }

    public async Task RevokeTokenAsync(Guid tokenId)
    {
        await _tokenRepo.RevokeAsync(tokenId);
    }

    public async Task<DeployToken?> GetValidatedAsync(string rawToken)
    {
        var hash = HashToken(rawToken);
        var token = await _tokenRepo.GetActiveByTokenHashAsync(hash, DateTime.UtcNow);
        if (token is null)
            return null;

        if (!token.ClientId.HasValue || !token.SiteId.HasValue)
            return null;

        return token;
    }

    public async Task<DeployToken?> GetValidatedByIdAsync(Guid tokenId, string rawToken)
    {
        var token = await _tokenRepo.GetByIdAsync(tokenId);
        if (token is null)
            return null;

        var hash = HashToken(rawToken);
        if (!string.Equals(token.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
            return null;

        var now = DateTime.UtcNow;
        if (token.RevokedAt is not null || token.ExpiresAt <= now)
            return null;

        if (token.MaxUses.HasValue && token.UsedCount >= token.MaxUses.Value)
            return null;

        if (!token.ClientId.HasValue || !token.SiteId.HasValue)
            return null;

        return token;
    }

    public async Task<(DeployToken Token, string RawToken)> CreateZeroTouchTokenAsync(Guid clientId, Guid siteId)
    {
        var rawToken = GenerateZeroTouchToken();
        var tokenHash = HashToken(rawToken);
        var prefix = rawToken[..12];

        var token = new DeployToken
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            SiteId = siteId,
            TokenHash = tokenHash,
            TokenPrefix = prefix,
            Description = "zero-touch",
            ExpiresAt = DateTime.UtcNow.Add(ZeroTouchTokenTtl),
            CreatedAt = DateTime.UtcNow,
            UsedCount = 0,
            MaxUses = 1
        };

        await _tokenRepo.CreateAsync(token);
        return (token, rawToken);
    }

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var tokenBody = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return $"mdz_deploy_{tokenBody}";
    }

    private static string GenerateZeroTouchToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var tokenBody = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return $"mdz_zt_{tokenBody}";
    }

    private static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
