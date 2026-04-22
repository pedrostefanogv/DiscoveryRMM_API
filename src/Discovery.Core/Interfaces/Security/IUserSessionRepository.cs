using Discovery.Core.Entities.Security;

namespace Discovery.Core.Interfaces.Security;

public interface IUserSessionRepository
{
    Task<UserSession?> GetByIdAsync(Guid id);
    Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash);
    Task<IEnumerable<UserSession>> GetActiveByUserIdAsync(Guid userId);
    Task<UserSession> CreateAsync(UserSession session);
    Task<bool> RevokeAsync(Guid sessionId);
    Task<bool> RevokeAllByUserIdAsync(Guid userId);
    Task<bool> UpdateAccessTokenHashAsync(Guid sessionId, string newHash, DateTime newExpiry);
}
