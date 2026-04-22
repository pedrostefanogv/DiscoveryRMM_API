using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class AgentSyncManifestDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public int RecommendedPollSeconds { get; set; }
    public int MaxStaleSeconds { get; set; }
    public IReadOnlyList<AgentSyncManifestResourceDto> Resources { get; set; } = [];
}

public class AgentSyncManifestResourceDto
{
    public SyncResourceType Resource { get; set; }
    public string? Variant { get; set; }
    public string Revision { get; set; } = string.Empty;
    public int RecommendedSyncInSeconds { get; set; }
    public string Endpoint { get; set; } = string.Empty;
}
