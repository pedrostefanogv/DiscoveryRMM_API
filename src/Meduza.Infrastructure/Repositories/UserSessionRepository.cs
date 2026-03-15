using Meduza.Core.Entities.Security;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces.Security;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class UserSessionRepository : IUserSessionRepository
{
    private readonly MeduzaDbContext _db;
    public UserSessionRepository(MeduzaDbContext db) => _db = db;

    public Task<UserSession?> GetByIdAsync(Guid id)
        => _db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);

    public Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash)
        => _db.UserSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash && s.RevokedAt == null);

    public async Task<IEnumerable<UserSession>> GetActiveByUserIdAsync(Guid userId)
        => await _db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

    public async Task<UserSession> CreateAsync(UserSession session)
    {
        if (session.Id == Guid.Empty) session.Id = IdGenerator.NewId();
        session.CreatedAt = DateTime.UtcNow;
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    public async Task<bool> RevokeAsync(Guid sessionId)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.UserSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now));
        return rows > 0;
    }

    public async Task<bool> RevokeAllByUserIdAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now));
        return rows > 0;
    }

    public async Task<bool> UpdateAccessTokenHashAsync(Guid sessionId, string newHash, DateTime newExpiry)
    {
        var rows = await _db.UserSessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AccessTokenHash, newHash)
                .SetProperty(x => x.ExpiresAt, newExpiry));
        return rows > 0;
    }
}
