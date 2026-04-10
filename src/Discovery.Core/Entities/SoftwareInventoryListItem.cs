namespace Discovery.Core.Entities;

public class SoftwareInventoryListItem
{
    public Guid InventoryId { get; set; }
    public Guid AgentId { get; set; }
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    public Guid SoftwareId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallId { get; set; }
    public string? Serial { get; set; }
    public string? Source { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? AgentDisplayName { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public DateTime CollectedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
