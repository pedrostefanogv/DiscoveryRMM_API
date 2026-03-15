namespace Meduza.Core.Entities.Security;

/// <summary>
/// Token de integração de API.
/// O accessKey é mostrado SOMENTE no momento da criação (armazena apenas o hash).
/// Formato de autenticação: Authorization: ApiKey {TokenIdPublic}.{AccessKey}
/// </summary>
public class ApiToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Nome descritivo dado pelo usuário (ex: "CI Pipeline").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Identificador público do token. Formato: mzt_{uuid32chars}.</summary>
    public string TokenIdPublic { get; set; } = string.Empty;

    /// <summary>SHA-256 da access key. A access key original nunca é armazenada.</summary>
    public string AccessKeyHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
    public bool IsValid => IsActive && !IsExpired;
}
