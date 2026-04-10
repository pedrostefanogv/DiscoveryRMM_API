namespace Discovery.Core.DTOs;

// ─── Request / Response do agente ───────────────────────────────────────────

public record P2pSeedPlanResponseDto(
    string? SiteId,
    string GeneratedAtUtc,
    P2pSeedPlanDto Plan);

public record P2pSeedPlanDto(
    int TotalAgents,
    int ConfiguredPercent,
    int MinSeeds,
    int SelectedSeeds);

// ─── Telemetria enviada pelo agente ─────────────────────────────────────────

public class P2pTelemetryRequest
{
    /// <summary>Auxiliar — servidor resolve via token. Se informado e divergir → 400.</summary>
    public string? AgentId { get; set; }

    /// <summary>Auxiliar — servidor resolve via token. Se informado e divergir → 400.</summary>
    public string? SiteId { get; set; }

    /// <summary>RFC3339 com timezone — campo obrigatório.</summary>
    public string? CollectedAtUtc { get; set; }

    public P2pMetricsDto? Metrics { get; set; }
    public P2pSeedPlanDto? CurrentSeedPlan { get; set; }
}

public class P2pMetricsDto
{
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
}

// ─── Distribution status ─────────────────────────────────────────────────────

public class P2pDistributionStatusItem
{
    public string ArtifactId { get; set; } = string.Empty;
    public string? ArtifactName { get; set; }
    public int PeerCount { get; set; }
    /// <summary>Omitido quando PeerCount > 500.</summary>
    public List<string>? PeerAgentIds { get; set; }
    public string LastUpdatedUtc { get; set; } = string.Empty;
}

// ─── Ops / Dashboard ────────────────────────────────────────────────────────

public class P2pOverviewDto
{
    public string Scope { get; set; } = "global";
    public string? ScopeId { get; set; }
    public string Window { get; set; } = "24h";
    public P2pKpisDto Kpis { get; set; } = new();
    public string Health { get; set; } = "ok";
    public string UpdatedAtUtc { get; set; } = string.Empty;
}

public class P2pKpisDto
{
    public int ActiveAgents { get; set; }
    public int ActiveSeeders { get; set; }
    public double ReplicationSuccessRate { get; set; }
    public long BytesServedDelta { get; set; }
    public long BytesDownloadedDelta { get; set; }
    public double QueuePressure { get; set; }
    public int ArtifactsWithPeers { get; set; }
    public string? LastTelemetryAtUtc { get; set; }
}

public class P2pAgentRankingItem
{
    public string AgentId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public double HealthScore { get; set; }
    public long ReplicationsStartedDelta { get; set; }
    public double SuccessRate { get; set; }
    public double FailureRate { get; set; }
    public long BytesServedDelta { get; set; }
    public long BytesDownloadedDelta { get; set; }
    public double ActiveReplicationsAvg { get; set; }
    public double QueuedReplicationsAvg { get; set; }
    public string? LastTelemetryAtUtc { get; set; }
}

public class P2pTimeseriesDto
{
    public string Metric { get; set; } = string.Empty;
    public string Unit { get; set; } = "count";
    public List<P2pTimeseriesPoint> Points { get; set; } = new();
    public P2pTimeseriesSummary Summary { get; set; } = new();
}

public record P2pTimeseriesPoint(string TsUtc, double Value);

public class P2pTimeseriesSummary
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Avg { get; set; }
    public double P95 { get; set; }
    public double Total { get; set; }
}

public class P2pSeedPlanHistoryItem
{
    public string SiteId { get; set; } = string.Empty;
    public int TotalAgents { get; set; }
    public int ConfiguredPercent { get; set; }
    public int MinSeeds { get; set; }
    public int SelectedSeeds { get; set; }
    public string GeneratedAtUtc { get; set; } = string.Empty;
}

// ─── Erro padrão P2P ────────────────────────────────────────────────────────

public class P2pErrorDetail
{
    public string? Field { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
}
