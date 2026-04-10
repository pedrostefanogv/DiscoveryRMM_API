using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IDeployTokenRepository
{
    Task<DeployToken?> GetByIdAsync(Guid id);
    Task<DeployToken?> GetByTokenHashAsync(string tokenHash);
    Task<DeployToken> CreateAsync(DeployToken token);
    Task<DeployToken?> TryUseByTokenHashAsync(string tokenHash, DateTime now);
    Task RevokeAsync(Guid id);
    Task<IEnumerable<DeployToken>> GetByClientSiteAsync(Guid clientId, Guid siteId);
}
