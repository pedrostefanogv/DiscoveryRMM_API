using Discovery.Core.Entities.Security;

namespace Discovery.Core.Interfaces.Security;

public interface IUserSessionRepository
{
    Task<UserSession?> GetByIdAsync(Guid id);
    Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash);

    /// <summary>
    /// Busca sessão pelo hash do refresh token, incluindo sessões revogadas
    /// que ainda estão dentro do grace period (rotação com tolerância).
    /// </summary>
    Task<UserSession?> GetByRefreshTokenHashWithGracePeriodAsync(string refreshTokenHash);

    Task<IEnumerable<UserSession>> GetActiveByUserIdAsync(Guid userId);
    Task<UserSession> CreateAsync(UserSession session);
    Task<bool> RevokeAsync(Guid sessionId);

    /// <summary>
    /// Revoga a sessão mas mantém o refresh token aceito por um grace period
    /// (para suportar renovação concorrente de múltiplas abas).
    /// </summary>
    Task<bool> RevokeWithGracePeriodAsync(Guid sessionId, TimeSpan gracePeriod);

    Task<bool> RevokeAllByUserIdAsync(Guid userId);
    Task<bool> UpdateAccessTokenHashAsync(Guid sessionId, string newHash, DateTime newExpiry);
}
