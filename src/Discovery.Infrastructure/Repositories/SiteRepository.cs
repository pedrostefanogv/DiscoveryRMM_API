using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class SiteRepository : ISiteRepository
{
    private readonly DiscoveryDbContext _db;

    public SiteRepository(DiscoveryDbContext db) => _db = db;

    public async Task<Site?> GetByIdAsync(Guid id)
    {
        return await _db.Sites
            .AsNoTracking()
            .SingleOrDefaultAsync(site => site.Id == id);
    }

    public async Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
    {
        IQueryable<Site> query = _db.Sites
            .AsNoTracking()
            .Where(site => site.ClientId == clientId);

        if (!includeInactive)
            query = query.Where(site => site.IsActive);

        return await query
            .OrderBy(site => site.Name)
            .ToListAsync();
    }

    public async Task<Site> CreateAsync(Site site)
    {
        site.Id = IdGenerator.NewId();
        site.CreatedAt = DateTime.UtcNow;
        site.UpdatedAt = DateTime.UtcNow;

        _db.Sites.Add(site);
        await _db.SaveChangesAsync();
        return site;
    }

    public async Task UpdateAsync(Site site)
    {
        var existingSite = await _db.Sites.SingleOrDefaultAsync(existing => existing.Id == site.Id);
        if (existingSite is null)
            return;

        existingSite.ClientId = site.ClientId;
        existingSite.Name = site.Name;
        existingSite.Notes = site.Notes;
        existingSite.IsActive = site.IsActive;
        existingSite.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var now = DateTime.UtcNow;

        await _db.Sites
            .Where(site => site.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(site => site.IsActive, _ => false)
                .SetProperty(site => site.UpdatedAt, _ => now));
    }
}
