using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IDeployTokenService
{
    Task<(DeployToken Token, string RawToken)> CreateTokenAsync(string? description, int? expiresInHours = null, bool multiUse = false);
    Task<DeployToken?> TryUseTokenAsync(string rawToken);
    Task RevokeTokenAsync(Guid tokenId);
}
