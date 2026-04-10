namespace Discovery.Core.Entities;

/// <summary>
/// Token de deploy para instalacao de agents.
/// </summary>
public class DeployToken
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int UsedCount { get; set; }
    public int? MaxUses { get; set; }
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    public bool IsDepleted => MaxUses.HasValue && UsedCount >= MaxUses.Value;
    public bool IsValid => !IsRevoked && !IsExpired && !IsDepleted;
}
