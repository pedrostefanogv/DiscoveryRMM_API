using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.SoftwareInventory.Queries;

// ── Agent-scoped (legacy) ────────────────────────────────────────────
public sealed record ListAgentSoftwareQuery(Guid AgentId) : IQuery<Result<SoftwareInventoryDto>>;
public sealed record GetSoftwareInventorySnapshotQuery : IQuery<Result<SnapshotDto>>;

public sealed record SoftwareInventoryDto(
    IReadOnlyList<SoftwareItemDto> Current,
    SnapshotDto? Snapshot
);

public sealed record SoftwareItemDto(
    Guid InventoryId, string Name, string? Version, string? Publisher,
    string? InstallDate, DateTime CollectedAt
);

public sealed record SnapshotDto(
    Guid AgentId, int TotalInstalled, DateTime? LastCollectedAt
);

// ── Scope-based (global / client / site) ─────────────────────────────

public enum SoftwareInventoryScope { Global, Client, Site }

public sealed record ListSoftwareInventoryQuery(
    SoftwareInventoryScope Scope,
    Guid? ScopeId,
    string? Cursor,
    int Limit,
    string? Search,
    bool Descending
) : IQuery<Result<SoftwareInventoryListDto>>;

public sealed record GetSoftwareInventoryScopeSnapshotQuery(
    SoftwareInventoryScope Scope,
    Guid? ScopeId
) : IQuery<Result<ScopeSnapshotDto>>;

public sealed record SoftwareInventoryListDto(
    IReadOnlyList<SoftwareInventoryCatalogItemDto> Items,
    string? NextCursor,
    bool HasMore
);

public sealed record SoftwareInventoryCatalogItemDto(
    Guid SoftwareId, string Name, string? Publisher, string? Source,
    int InstalledCount, DateTime? FirstSeenAt, DateTime? LastCollectedAt,
    DateTime? LastSeenAt
);

public sealed record ListSoftwareInstallationsQuery(
    Guid SoftwareId,
    SoftwareInventoryScope Scope,
    Guid? ScopeId,
    string? Cursor,
    int Limit,
    bool Descending
) : IQuery<Result<SoftwareInstallationsDto>>;

public sealed record SoftwareInstallationsDto(
    IReadOnlyList<SoftwareInstallationItemDto> Items,
    string? NextCursor,
    bool HasMore
);

public sealed record SoftwareInstallationItemDto(
    Guid AgentId, string Hostname, string? AgentDisplayName,
    Guid SiteId, string SiteName, Guid ClientId, string ClientName,
    string? Version, string? Source,
    DateTime CollectedAt, DateTime FirstSeenAt, DateTime LastSeenAt
);

public sealed record ScopeSnapshotDto(
    int TotalInstalled, int DistinctSoftware, int DistinctAgents,
    DateTime? LastCollectedAt
);