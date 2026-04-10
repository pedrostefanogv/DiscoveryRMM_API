namespace Discovery.Core.Entities;

/// <summary>
/// Plano de seed calculado por site. Recalculado a cada 15 minutos.
/// </summary>
public class P2pSeedPlan
{
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    public int TotalAgents { get; set; }
    public int ConfiguredPercent { get; set; } = 10;
    public int MinSeeds { get; set; } = 2;
    public int SelectedSeeds { get; set; }
    public DateTime GeneratedAt { get; set; }
}
