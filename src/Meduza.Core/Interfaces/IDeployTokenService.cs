using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IDeployTokenService
{
    Task<(DeployToken Token, string RawToken)> CreateTokenAsync(Guid clientId, Guid siteId, string? description, int? expiresInHours = null, bool multiUse = false);
    Task<DeployToken?> TryUseTokenAsync(string rawToken);
    Task RevokeTokenAsync(Guid tokenId);
    /// <summary>
    /// Validates that rawToken matches the stored hash for the given token ID, without consuming the token.
    /// Returns the token if valid, null otherwise.
    /// </summary>
    Task<DeployToken?> GetValidatedByIdAsync(Guid tokenId, string rawToken);
}
