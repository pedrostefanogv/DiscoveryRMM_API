using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Agents.Inventory.Queries;

public sealed record GetAgentHardwareQuery(Guid AgentId) : IQuery<Result<AgentHardwareDto>>;
public sealed record GetAgentHardwareReportQuery(Guid AgentId) : IQuery<Result<AgentHardwareReportDto>>;
public sealed record GetAgentHardwareComponentsQuery(Guid AgentId) : IQuery<Result<AgentHardwareComponentsDto>>;

/// <summary>
/// Página por cursor (ponteiro) sobre listas derivadas do snapshot de
/// componentes do agente (portas em escuta / conexões abertas). O cursor é o
/// índice do próximo item na lista já filtrada e ordenada do snapshot corrente
/// — o snapshot é imutável entre coletas, então o ponteiro é estável dentro da
/// coleta. TotalCount reflete o filtro ativo, para o rodapé da tabela.
/// </summary>
public sealed record AgentNetworkPageDto<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    string? Cursor,
    string? NextCursor,
    bool HasMore,
    int Limit);

public sealed record GetAgentListeningPortsPageQuery(
    Guid AgentId,
    string? Cursor = null,
    int Limit = 50,
    string? Search = null) : IQuery<Result<AgentNetworkPageDto<AgentHardwareListeningPortDto>>>;

public sealed record GetAgentOpenSocketsPageQuery(
    Guid AgentId,
    string? Cursor = null,
    int Limit = 50,
    string? Search = null) : IQuery<Result<AgentNetworkPageDto<AgentHardwareOpenSocketDto>>>;
public sealed record GetAgentSoftwareQuery(Guid AgentId, string? Cursor = null, int Limit = 100, string? Search = null, bool Descending = false) : IQuery<Result<CursorPageDto<AgentSoftwareItemDto>>>;
public sealed record GetAgentSoftwareSnapshotQuery(Guid AgentId) : IQuery<Result<AgentSoftwareSnapshotDto>>;

/// <summary>
/// Paginação por offset (número de página) para o detalhe do agente — suporta
/// navegação aleatória de páginas com total filtrado, sem fetch-all no cliente.
/// </summary>
public sealed record GetAgentSoftwarePageQuery(
    Guid AgentId,
    int Page = 1,
    int PageSize = 50,
    string? Search = null,
    bool Descending = false) : IQuery<Result<AgentSoftwarePageDto>>;

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
    string? OsBuild,
    string? OsArchitecture
);

/// <summary>
/// DTO agregado que combina hardware info + components em um unico payload.
/// Alinhado com o contrato <c>HardwareReport</c> do frontend (DiscoveryRMM_Site).
/// </summary>
public sealed record AgentHardwareReportDto(
    AgentHardwareDto? Hardware,
    List<AgentHardwarePrinterDto> Printers,
    List<AgentHardwareListeningPortDto> ListeningPorts,
    List<AgentHardwareOpenSocketDto> OpenSockets,
    List<AgentHardwareDiskDto> Disks,
    List<AgentHardwareNetworkAdapterDto> NetworkAdapters,
    List<AgentHardwareMemoryModuleDto> MemoryModules,
    DateTime? CollectedAt
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

/// <summary>
/// Página de inventário de software com total filtrado — fonte única de
/// verdade para paginação server-side no detalhe do agente (o total reflete
/// o filtro de busca ativo, corrigindo o número de páginas sob filtro).
/// </summary>
public sealed record AgentSoftwarePageDto(
    IReadOnlyList<AgentSoftwareItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public sealed record AgentHardwareComponentsDto(
    List<AgentHardwarePrinterDto> Printers,
    List<AgentHardwareListeningPortDto> ListeningPorts,
    List<AgentHardwareOpenSocketDto> OpenSockets,
    List<AgentHardwareDiskDto> Disks,
    List<AgentHardwareNetworkAdapterDto> NetworkAdapters,
    List<AgentHardwareMemoryModuleDto> MemoryModules,
    List<AgentStartupItemDto> StartupItems,
    List<AgentScheduledTaskDto> ScheduledTasks,
    DateTime? CollectedAt
);

public sealed record AgentStartupItemDto(
    string Name,
    string Path,
    string Args,
    string Type,
    string Source,
    string Status,
    string Username,
    string Detail,
    string Hive
);

public sealed record AgentScheduledTaskDto(
    string TaskPath,
    string TaskName,
    string State,
    string Status,
    string Author,
    string ActionPath,
    string ActionArgs,
    string TriggerType,
    string TriggerDesc,
    string NextRunTime,
    string LastRunTime,
    long LastResult
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
    string? State,
    DateTime CollectedAt
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
    string? Family,
    string? State,
    DateTime CollectedAt
);

public sealed record AgentHardwareDiskDto(
    string DriveLetter,
    string? Label,
    string? FileSystem,
    long TotalSizeBytes,
    long FreeSpaceBytes,
    string? MediaType,
    string? SmartStatus = null,
    int? TemperatureC = null,
    int? PowerOnHours = null,
    int? ReallocatedSectors = null
);

public sealed record AgentHardwareNetworkAdapterDto(
    string Name,
    string? MacAddress,
    string? IpAddress,
    string? Ipv6Address,
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