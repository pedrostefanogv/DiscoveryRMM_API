namespace Meduza.Core.Entities;

/// <summary>
/// Configurações do tenant (client) — inclui SLA de token, preferências de workflow, etc.
/// </summary>
public class TenantSettings
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public int TokenExpirationDays { get; set; } = 365;
    public int MaxTokensPerAgent { get; set; } = 3;
    public int AgentHeartbeatIntervalSeconds { get; set; } = 60;
    public int AgentOfflineThresholdSeconds { get; set; } = 300;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
