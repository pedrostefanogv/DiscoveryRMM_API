using System.Text.Json;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Hardware;

// Queries
public sealed record GetAgentHardwareQuery(Guid AgentId) : IQuery<Result<AgentHardwarePayloadDto>>;
public sealed record AgentHardwarePayloadDto(
    object? Hardware, object? Disks, object? NetworkAdapters, object? MemoryModules,
    object? Printers, object? ListeningPorts, object? OpenSockets);

// Commands
public sealed record ReportAgentHardwareCommand(
    Guid AgentId,
    string? Hostname, string? DisplayName, string? MeshCentralNodeId,
    string? Status, string? OperatingSystem, string? OsVersion, string? AgentVersion, string? CommitHash,
    string? LastIpAddress, string? MacAddress,
    object? Hardware, object? Components, JsonElement? InventoryRaw,
    string? InventorySchemaVersion, DateTime? InventoryCollectedAt, int? MachineScore
) : ICommand<Result<VoidResult>>;