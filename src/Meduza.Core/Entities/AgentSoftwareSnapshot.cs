namespace Meduza.Core.Entities;

public class AgentSoftwareSnapshot
{
    public Guid AgentId { get; set; }
    public int TotalInstalled { get; set; }
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastCollectedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
