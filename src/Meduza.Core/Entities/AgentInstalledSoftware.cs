namespace Meduza.Core.Entities;

public class AgentInstalledSoftware
{
    public Guid InventoryId { get; set; }
    public Guid AgentId { get; set; }
    public Guid SoftwareId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallId { get; set; }
    public string? Serial { get; set; }
    public string? Source { get; set; }
    public DateTime CollectedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
