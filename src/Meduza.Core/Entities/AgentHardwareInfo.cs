namespace Meduza.Core.Entities;

public class AgentHardwareInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    // Inventario raw (osquery)
    public string? InventoryRaw { get; set; }
    public string? HardwareComponentsJson { get; set; }
    public string? InventorySchemaVersion { get; set; }
    public DateTime? InventoryCollectedAt { get; set; }

    // Placa-mãe
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? MotherboardManufacturer { get; set; }
    public string? MotherboardModel { get; set; }
    public string? MotherboardSerialNumber { get; set; }

    // Processador
    public string? Processor { get; set; }
    public int? ProcessorCores { get; set; }
    public int? ProcessorThreads { get; set; }
    public string? ProcessorArchitecture { get; set; }
    public int? ProcessorTdpWatts { get; set; }
    public string? ProcessorSocket { get; set; }
    public decimal? ProcessorFrequencyGhz { get; set; }
    public string? ProcessorReleaseDate { get; set; }

    // Memória (total)
    public long? TotalMemoryBytes { get; set; }

    // GPU / Placa de Vídeo
    public string? GpuModel { get; set; }
    public long? GpuMemoryBytes { get; set; }
    public string? GpuDriverVersion { get; set; }

    // Discos
    public int? TotalDisksCount { get; set; }

    // BIOS / Firmware
    public string? BiosVersion { get; set; }
    public string? BiosManufacturer { get; set; }
    public string? BiosDate { get; set; }
    public string? BiosSerialNumber { get; set; }

    // OS
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? OsArchitecture { get; set; }

    public DateTime CollectedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
