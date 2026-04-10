namespace Discovery.Core.Entities;

/// <summary>
/// Token de autenticação para um agent. 
/// Mantemos apenas um token ativo por agent.
/// </summary>
public class AgentToken
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
    public bool IsValid => !IsRevoked && !IsExpired;
}
