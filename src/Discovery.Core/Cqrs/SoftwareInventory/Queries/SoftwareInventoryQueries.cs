using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.SoftwareInventory.Queries;

public sealed record ListAgentSoftwareQuery(Guid AgentId) : IQuery<Result<SoftwareInventoryDto>>;

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