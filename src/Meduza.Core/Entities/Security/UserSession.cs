namespace Meduza.Core.Entities.Security;

/// <summary>
/// Sessão de usuário autenticado.
/// Persiste access token hash e refresh token hash para revogação.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 do access token JWT (para busca e revogação rápida).</summary>
    public string AccessTokenHash { get; set; } = string.Empty;

    /// <summary>SHA-256 do refresh token (opaque byte[32]).</summary>
    public string RefreshTokenHash { get; set; } = string.Empty;

    /// <summary>Indica se o 2FA foi verificado nesta sessão.</summary>
    public bool MfaVerified { get; set; } = false;

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    public bool IsValid => !IsRevoked && !IsExpired;
}
