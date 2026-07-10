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
    IReadOnlyList<SoftwareInventoryItemDto> Items,
    string? NextCursor,
    bool HasMore
);

public sealed record SoftwareInventoryItemDto(
    Guid InventoryId, Guid AgentId, Guid SiteId, Guid ClientId,
    Guid SoftwareId, string Name, string? Version, string? Publisher,
    string? InstallDate, string Hostname, string? AgentDisplayName,
    string SiteName, string ClientName, DateTime CollectedAt
);

public sealed record ScopeSnapshotDto(
    int TotalInstalled, int DistinctSoftware, int DistinctAgents,
    DateTime? LastCollectedAt
);