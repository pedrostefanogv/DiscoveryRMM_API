namespace Discovery.Core.Entities;

/// <summary>
/// Snapshot de telemetria P2P enviado pelo agente a cada ~5 minutos.
/// Contadores são cumulativos desde o início do processo.
/// </summary>
public class P2pAgentTelemetry
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Timestamp de coleta das métricas no agente (RFC3339).</summary>
    public DateTime CollectedAt { get; set; }

    /// <summary>Timestamp de recebimento pelo servidor.</summary>
    public DateTime ReceivedAt { get; set; }

    // --- Métricas cumulativas ---
    public int PublishedArtifacts { get; set; }
    public long ReplicationsStarted { get; set; }
    public long ReplicationsSucceeded { get; set; }
    public long ReplicationsFailed { get; set; }
    public long BytesServed { get; set; }
    public long BytesDownloaded { get; set; }
    public int QueuedReplications { get; set; }
    public int ActiveReplications { get; set; }
    public long AutoDistributionRuns { get; set; }
    public long CatalogRefreshRuns { get; set; }
    public long ChunkedDownloads { get; set; }
    public long ChunksDownloaded { get; set; }

    // --- Seed plan vigente no momento da coleta ---
    public int PlanTotalAgents { get; set; }
    public int PlanConfiguredPercent { get; set; }
    public int PlanMinSeeds { get; set; }
    public int PlanSelectedSeeds { get; set; }
}
