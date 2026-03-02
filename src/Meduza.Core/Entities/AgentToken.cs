namespace Meduza.Core.Entities;

/// <summary>
/// Token de autenticação para um agent. 
/// Cada agent pode ter múltiplos tokens (rotação).
/// </summary>
public class AgentToken
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    public bool IsValid => !IsRevoked && !IsExpired;
}
