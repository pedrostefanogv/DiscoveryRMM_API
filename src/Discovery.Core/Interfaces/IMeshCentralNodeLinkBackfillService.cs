namespace Discovery.Core.Interfaces;

public interface IMeshCentralNodeLinkBackfillService
{
    Task<MeshCentralNodeLinkBackfillReport> RunBackfillAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        bool applyChanges = false,
        CancellationToken cancellationToken = default);
}

public sealed class MeshCentralNodeLinkBackfillReport
{
    public required bool ApplyChanges { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required DateTime FinishedAtUtc { get; init; }
    public required int TotalAgents { get; init; }
    public int UpdatedAgents { get; init; }
    public int VerifiedAgents { get; init; }
    public int MissingAgents { get; init; }
    public int AmbiguousAgents { get; init; }
    public required IReadOnlyCollection<MeshCentralNodeLinkBackfillItem> Items { get; init; }
}

public sealed class MeshCentralNodeLinkBackfillItem
{
    public required Guid AgentId { get; init; }
    public required Guid SiteId { get; init; }
    public required string Hostname { get; init; }
    public string? DisplayName { get; init; }
    public string? CurrentNodeId { get; init; }
    public string? SuggestedNodeId { get; init; }
    public required string Status { get; init; }
    public bool Applied { get; init; }
    public required IReadOnlyCollection<string> CandidateNodeIds { get; init; }
    public string? Error { get; init; }
}