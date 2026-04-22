namespace Discovery.Core.Entities;

public class AgentSoftwareInventory
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SoftwareId { get; set; }
    public DateTime CollectedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? Version { get; set; }
    public bool IsPresent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}