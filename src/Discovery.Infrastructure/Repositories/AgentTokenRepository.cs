using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentTokenRepository : IAgentTokenRepository
{
    private readonly DiscoveryDbContext _db;

    public AgentTokenRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AgentToken?> GetByIdAsync(Guid id)
    {
        return await _db.AgentTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.Id == id);
    }

    public async Task<AgentToken?> GetByTokenHashAsync(string tokenHash)
    {
        return await _db.AgentTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash);
    }

    public async Task<IEnumerable<AgentToken>> GetByAgentIdAsync(Guid agentId)
    {
        return await _db.AgentTokens
            .AsNoTracking()
            .Where(token => token.AgentId == agentId)
            .OrderByDescending(token => token.CreatedAt)
            .ToListAsync();
    }

    public async Task<AgentToken> CreateAsync(AgentToken token)
    {
        if (token.Id == Guid.Empty)
            token.Id = IdGenerator.NewId();

        if (token.CreatedAt == default)
            token.CreatedAt = DateTime.UtcNow;

        _db.AgentTokens.Add(token);
        await _db.SaveChangesAsync();
        return token;
    }

    public async Task UpdateLastUsedAsync(Guid id)
    {
        var now = DateTime.UtcNow;

        await _db.AgentTokens
            .Where(token => token.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.LastUsedAt, _ => now));
    }

    public async Task RevokeAsync(Guid id)
    {
        var now = DateTime.UtcNow;

        await _db.AgentTokens
            .Where(token => token.Id == id && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAt, _ => now));
    }

    public async Task RevokeAllByAgentIdAsync(Guid agentId)
    {
        var now = DateTime.UtcNow;

        await _db.AgentTokens
            .Where(token => token.AgentId == agentId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.RevokedAt, _ => now));
    }
}
