using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Agents.Inventory.Queries;

public sealed record GetAgentHardwareQuery(Guid AgentId) : IQuery<Result<AgentHardwareDto>>;
public sealed record GetAgentHardwareComponentsQuery(Guid AgentId) : IQuery<Result<AgentHardwareComponentsDto>>;
public sealed record GetAgentSoftwareQuery(Guid AgentId, string? Cursor = null, int Limit = 100, string? Search = null, bool Descending = false) : IQuery<Result<CursorPageDto<AgentSoftwareItemDto>>>;
public sealed record GetAgentSoftwareSnapshotQuery(Guid AgentId) : IQuery<Result<AgentSoftwareSnapshotDto>>;

public sealed record AgentHardwareDto(
    string Manufacturer,
    string Model,
    string? SerialNumber,
    string? BiosVersion,
    string? BiosManufacturer,
    string? BiosDate,
    long? TotalMemoryBytes,
    int? ProcessorCores,
    int? ProcessorThreads,
    string? Processor,
    string? ProcessorArchitecture,
    decimal? ProcessorFrequencyGhz,
    int? MachineScore,
    string? GpuModel,
    long? GpuMemoryBytes,
    string? OsName,
    string? OsVersion,
    string? OsArchitecture
);
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

public sealed record AgentHardwareComponentsDto(
    List<AgentHardwarePrinterDto> Printers,
    List<AgentHardwareListeningPortDto> ListeningPorts,
    List<AgentHardwareOpenSocketDto> OpenSockets,
    List<AgentHardwareDiskDto> Disks,
    List<AgentHardwareNetworkAdapterDto> NetworkAdapters,
    List<AgentHardwareMemoryModuleDto> MemoryModules,
    DateTime? CollectedAt
);

public sealed record AgentHardwarePrinterDto(
    string Name,
    string? DriverName,
    string? PortName,
    string? PrinterStatus,
    bool IsDefault,
    bool IsNetworkPrinter,
    bool Shared,
    string? ShareName,
    string? Location
);

public sealed record AgentHardwareListeningPortDto(
    string? ProcessName,
    int ProcessId,
    string? ProcessPath,
    string? Protocol,
    string? Address,
    int Port,
    string? State
);

public sealed record AgentHardwareOpenSocketDto(
    string? ProcessName,
    int ProcessId,
    string? ProcessPath,
    string? LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int RemotePort,
    string? Protocol,
    string? Family
);

public sealed record AgentHardwareDiskDto(
    string DriveLetter,
    string? Label,
    string? FileSystem,
    long TotalSizeBytes,
    long FreeSpaceBytes,
    string? MediaType
);

public sealed record AgentHardwareNetworkAdapterDto(
    string Name,
    string? MacAddress,
    string? IpAddress,
    string? SubnetMask,
    string? Gateway,
    List<string>? DnsServers,
    bool IsDhcpEnabled,
    string? AdapterType,
    string? Speed
);

public sealed record AgentHardwareMemoryModuleDto(
    string? Manufacturer,
    string? PartNumber,
    string? SerialNumber,
    long CapacityBytes,
    int? SpeedMhz,
    string? MemoryType,
    string? Slot,
    string? FormFactor,
    string? DeviceLocator
);