using Discovery.Core.Entities.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class ApiTokenRepository : IApiTokenRepository
{
    private readonly DiscoveryDbContext _db;
    public ApiTokenRepository(DiscoveryDbContext db) => _db = db;

    public Task<ApiToken?> GetByIdAsync(Guid id)
        => _db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);

    public Task<ApiToken?> GetByTokenIdPublicAsync(string tokenIdPublic)
        => _db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenIdPublic == tokenIdPublic);

    public async Task<IEnumerable<ApiToken>> GetByUserIdAsync(Guid userId)
        => await _db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

    public async Task<ApiToken> CreateAsync(ApiToken token)
    {
        if (token.Id == Guid.Empty) token.Id = IdGenerator.NewId();
        token.CreatedAt = DateTime.UtcNow;
        _db.ApiTokens.Add(token);
        await _db.SaveChangesAsync();
        return token;
    }

    public async Task<bool> RevokeAsync(Guid id, Guid userId)
    {
        var rows = await _db.ApiTokens
            .Where(t => t.Id == id && t.UserId == userId && t.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false));
        return rows > 0;
    }

    public async Task UpdateLastUsedAsync(Guid id)
    {
        var now = DateTime.UtcNow;
        await _db.ApiTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now));
    }
}
