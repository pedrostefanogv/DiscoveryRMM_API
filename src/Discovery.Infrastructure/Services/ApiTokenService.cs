using System.Security.Cryptography;
using System.Text;
using Discovery.Core.DTOs.ApiTokens;
using Discovery.Core.Entities.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Gerencia tokens de integração de API.
/// Formato: Authorization: ApiKey {tokenIdPublic}.{accessKey}
/// A accessKey é mostrada SOMENTE na criação. Apenas o SHA-256 é persisistido.
/// </summary>
public class ApiTokenService : IApiTokenService
{
    private readonly IApiTokenRepository _repo;

    public ApiTokenService(IApiTokenRepository repo)
    {
        _repo = repo;
    }

    public async Task<CreateApiTokenResponseDto> CreateTokenAsync(Guid userId, string name, DateTime? expiresAt)
    {
        var effectiveExpiresAt = expiresAt ?? DateTime.UtcNow.AddYears(1);

        // Token público identifier: mzt_ + UUID sem hífens
        var tokenIdPublic = "mzt_" + IdGenerator.NewId().ToString("N");

        // Access key: 32 bytes aleatórios, base64url prefixado
        var accessKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(accessKeyBytes);
        var accessKey = "mzk_" + Convert.ToBase64String(accessKeyBytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        // Armazena apenas o SHA-256 da access key
        var accessKeyHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(accessKey)));

        var token = new ApiToken
        {
            Id = IdGenerator.NewId(),
            UserId = userId,
            Name = name,
            TokenIdPublic = tokenIdPublic,
            AccessKeyHash = accessKeyHash,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = effectiveExpiresAt
        };

        await _repo.CreateAsync(token);

        return new CreateApiTokenResponseDto
        {
            Id = token.Id,
            Name = token.Name,
            TokenIdPublic = tokenIdPublic,
            AccessKey = accessKey,
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt
        };
    }

    public async Task<Guid?> AuthenticateAsync(string tokenIdPublicDotAccessKey)
    {
        var dotIndex = tokenIdPublicDotAccessKey.IndexOf('.');
        if (dotIndex < 0) return null;

        var tokenIdPublic = tokenIdPublicDotAccessKey[..dotIndex];
        var accessKey = tokenIdPublicDotAccessKey[(dotIndex + 1)..];

        var token = await _repo.GetByTokenIdPublicAsync(tokenIdPublic);
        if (token is null || !token.IsValid) return null;

        var expectedHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(accessKey)));

        // Comparação em tempo constante
        var match = CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(expectedHash),
            Convert.FromBase64String(token.AccessKeyHash));

        if (!match) return null;

        await _repo.UpdateLastUsedAsync(token.Id);
        return token.UserId;
    }

    public async Task<bool> RevokeAsync(Guid tokenId, Guid requestingUserId)
    {
        return await _repo.RevokeAsync(tokenId, requestingUserId);
    }

    public async Task<IEnumerable<ApiTokenSummaryDto>> GetByUserAsync(Guid userId)
    {
        var tokens = await _repo.GetByUserIdAsync(userId);
        return tokens.Select(t => new ApiTokenSummaryDto
        {
            Id = t.Id,
            Name = t.Name,
            TokenIdPublic = t.TokenIdPublic,
            IsActive = t.IsActive,
            CreatedAt = t.CreatedAt,
            LastUsedAt = t.LastUsedAt,
            ExpiresAt = t.ExpiresAt
        });
    }
}
