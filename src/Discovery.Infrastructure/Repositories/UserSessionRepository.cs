using Discovery.Core.Entities.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class UserSessionRepository : IUserSessionRepository
{
    private readonly DiscoveryDbContext _db;
    public UserSessionRepository(DiscoveryDbContext db) => _db = db;

    public Task<UserSession?> GetByIdAsync(Guid id)
        => _db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);

    public Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash)
        => _db.UserSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash
                                   && s.RevokedAt == null);

    /// <summary>
    /// Busca uma sessão pelo hash do refresh token, incluindo sessões revogadas
    /// que ainda estão dentro do grace period (rotação com tolerância).
    /// </summary>
    public Task<UserSession?> GetByRefreshTokenHashWithGracePeriodAsync(string refreshTokenHash)
        => _db.UserSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash
                                   && (s.RevokedAt == null
                                       || (s.RefreshTokenGracePeriodUntil.HasValue
                                           && s.RefreshTokenGracePeriodUntil.Value > DateTime.UtcNow)));

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

    /// <summary>
    /// Marca a sessão como revogada mas define um grace period durante o qual
    /// o refresh token antigo ainda é aceito (para suportar múltiplas abas).
    /// </summary>
    public async Task<bool> RevokeWithGracePeriodAsync(Guid sessionId, TimeSpan gracePeriod)
    {
        var now = DateTime.UtcNow;
        var graceUntil = now + gracePeriod;
        var rows = await _db.UserSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.RefreshTokenGracePeriodUntil, graceUntil));
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
