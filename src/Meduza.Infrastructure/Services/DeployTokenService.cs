using System.Security.Cryptography;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;

namespace Meduza.Infrastructure.Services;

public class DeployTokenService : IDeployTokenService
{
    private readonly IDeployTokenRepository _tokenRepo;

    public DeployTokenService(IDeployTokenRepository tokenRepo)
    {
        _tokenRepo = tokenRepo;
    }

    public async Task<(DeployToken Token, string RawToken)> CreateTokenAsync(string? description, int? expiresInHours = null, bool multiUse = false)
    {
        var hours = expiresInHours ?? 4;
        int? maxUses = multiUse ? null : 1;

        var rawToken = GenerateRawToken();
        var tokenHash = HashToken(rawToken);
        var prefix = rawToken[..12];

        var token = new DeployToken
        {
            Id = IdGenerator.NewId(),
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
        return await _tokenRepo.TryUseByTokenHashAsync(hash, DateTime.UtcNow);
    }

    public async Task RevokeTokenAsync(Guid tokenId)
    {
        await _tokenRepo.RevokeAsync(tokenId);
    }

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var tokenBody = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return $"mdz_deploy_{tokenBody}";
    }

    private static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
