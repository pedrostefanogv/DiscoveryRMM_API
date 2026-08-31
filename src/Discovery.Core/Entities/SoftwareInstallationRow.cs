namespace Discovery.Core.Entities;

/// <summary>Instalação de um software específico (para o modal de detalhes).</summary>
public class SoftwareInstallationRow
{
    public Guid AgentId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? AgentDisplayName { get; set; }
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Source { get; set; }
    public DateTime CollectedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
