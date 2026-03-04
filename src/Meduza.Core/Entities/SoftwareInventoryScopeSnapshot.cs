namespace Meduza.Core.Entities;

public class SoftwareInventoryScopeSnapshot
{
    public int TotalInstalled { get; set; }
    public int DistinctSoftware { get; set; }
    public int DistinctAgents { get; set; }
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastCollectedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
