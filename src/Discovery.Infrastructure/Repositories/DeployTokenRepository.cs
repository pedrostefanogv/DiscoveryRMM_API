using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class DeployTokenRepository : IDeployTokenRepository
{
    private readonly DiscoveryDbContext _db;

    public DeployTokenRepository(DiscoveryDbContext db) => _db = db;

    public async Task<DeployToken?> GetByIdAsync(Guid id)
    {
        return await _db.DeployTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.Id == id);
    }

    public async Task<DeployToken?> GetByTokenHashAsync(string tokenHash)
    {
        return await _db.DeployTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash);
    }

    public async Task<DeployToken?> GetActiveByTokenHashAsync(string tokenHash, DateTime now)
    {
        return await _db.DeployTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash
                && token.RevokedAt == null
                && token.ExpiresAt > now
                && (token.MaxUses == null || token.UsedCount < token.MaxUses));
    }

    public async Task<DeployToken> CreateAsync(DeployToken token)
    {
        _db.DeployTokens.Add(token);
        await _db.SaveChangesAsync();
        return token;
    }

    public async Task<DeployToken?> TryUseByTokenHashAsync(string tokenHash, DateTime now)
    {
        var affected = await _db.DeployTokens
            .Where(token => token.TokenHash == tokenHash
                && token.RevokedAt == null
                && token.ExpiresAt > now
                && (token.MaxUses == null || token.UsedCount < token.MaxUses))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.UsedCount, token => token.UsedCount + 1)
                .SetProperty(token => token.LastUsedAt, _ => now));

        if (affected == 0)
            return null;

        return await _db.DeployTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash);
    }

    public async Task RevokeAsync(Guid id)
    {
        var now = DateTime.UtcNow;

        await _db.DeployTokens
            .Where(token => token.Id == id && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAt, _ => now));
    }

    public async Task<IEnumerable<DeployToken>> GetByClientSiteAsync(Guid clientId, Guid siteId)
    {
        return await _db.DeployTokens
            .AsNoTracking()
            .Where(token => token.ClientId == clientId && token.SiteId == siteId)
            .OrderByDescending(token => token.CreatedAt)
            .ToListAsync();
    }
}
