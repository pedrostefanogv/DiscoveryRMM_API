namespace Meduza.Core.Entities;

public class AgentHardwareInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

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

    // Memória (total)
    public long? TotalMemoryBytes { get; set; }

    // BIOS / Firmware
    public string? BiosVersion { get; set; }
    public string? BiosManufacturer { get; set; }

    // OS
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? OsArchitecture { get; set; }

    public DateTime CollectedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
