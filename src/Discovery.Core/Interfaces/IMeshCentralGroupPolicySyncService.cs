namespace Discovery.Core.Interfaces;

public interface IMeshCentralGroupPolicySyncService
{
    Task<MeshCentralGroupPolicySiteStatus> GetSiteStatusAsync(Guid siteId, CancellationToken cancellationToken = default);

    Task<MeshCentralGroupPolicyBackfillReport> RunBackfillAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        bool applyChanges = false,
        CancellationToken cancellationToken = default);
}

public sealed class MeshCentralGroupPolicySiteStatus
{
    public required Guid ClientId { get; init; }
    public required Guid SiteId { get; init; }
    public required string ClientName { get; init; }
    public required string SiteName { get; init; }
    public required bool SupportEnabled { get; init; }
    public required string DesiredProfile { get; init; }
    public string? AppliedProfile { get; init; }
    public string? MeshId { get; init; }
    public string? GroupName { get; init; }
    public DateTime? AppliedAtUtc { get; init; }
    public bool HasDrift { get; init; }
    public string[] DriftReasons { get; init; } = [];
}

public sealed class MeshCentralGroupPolicyBackfillReport
{
    public DateTime StartedAtUtc { get; init; }
    public DateTime FinishedAtUtc { get; init; }
    public bool ApplyChanges { get; init; }
    public int TotalSites { get; init; }
    public int UpdatedSites { get; init; }
    public int DriftedSites { get; init; }
    public int FailedSites { get; init; }
    public IReadOnlyCollection<MeshCentralGroupPolicyBackfillItem> Items { get; init; } = [];
}

public sealed class MeshCentralGroupPolicyBackfillItem
{
    public required Guid ClientId { get; init; }
    public required Guid SiteId { get; init; }
    public required string ClientName { get; init; }
    public required string SiteName { get; init; }
    public required bool SupportEnabled { get; init; }
    public required string DesiredProfile { get; init; }
    public string? AppliedProfileBefore { get; init; }
    public string? AppliedProfileAfter { get; init; }
    public string? MeshIdBefore { get; init; }
    public string? MeshIdAfter { get; init; }
    public string? GroupNameBefore { get; init; }
    public string? GroupNameAfter { get; init; }
    public bool HasDrift { get; init; }
    public bool Applied { get; init; }
    public bool Success { get; init; }
    public string[] DriftReasons { get; init; } = [];
    public string? Error { get; init; }
}