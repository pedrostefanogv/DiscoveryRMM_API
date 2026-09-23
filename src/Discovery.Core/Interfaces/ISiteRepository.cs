using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ISiteRepository
{
    Task<Site?> GetByIdAsync(Guid id);
    Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false);
    Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false);
    Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false);
    Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false);
    Task<Site> CreateAsync(Site site);
    Task UpdateAsync(Site site);
    Task DeleteAsync(Guid id);
}
