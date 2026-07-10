using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Agents.Inventory.Queries;

public sealed record GetAgentHardwareQuery(Guid AgentId) : IQuery<Result<AgentHardwareDto>>;
public sealed record GetAgentSoftwareQuery(Guid AgentId, string? Cursor = null, int Limit = 100, string? Search = null, bool Descending = false) : IQuery<Result<CursorPageDto<AgentSoftwareItemDto>>>;
public sealed record GetAgentSoftwareSnapshotQuery(Guid AgentId) : IQuery<Result<AgentSoftwareSnapshotDto>>;

public sealed record AgentHardwareDto(string Manufacturer, string Model, string? SerialNumber, string? BiosVersion, int? TotalRamMb, int? CpuCores, string? CpuName, string? OsName, string? OsVersion);
public sealed record AgentSoftwareItemDto(
    Guid InventoryId,
    string Name,
    string? Version,
    string? Publisher,
    string? Source,
    string? InstallId,
    string? Serial,
    DateTime? InstallDate,
    DateTime? CollectedAt
);
public sealed record AgentSoftwareSnapshotDto(Guid AgentId, int TotalInstalled, DateTime? LastCollectedAt);