namespace Discovery.Core.Entities;

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

    // Machine score (0-100 baseline, no cap — higher = more powerful)
    // Calculated by the agent: 50% CPU + 50% RAM, reference 16c/32t + 64 GB ≈ 100
    public int? MachineScore { get; set; }

    // OS
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? OsArchitecture { get; set; }

    // Identidade do SO (Windows Server x estação de trabalho / domínio).
    // Todas NULLABLE de propósito: agentes antigos não enviam estes campos e
    // NULL significa "não reportado" — nunca "não é servidor". Consumidores
    // (site, filtros, relatórios) só devem tratar IsServer != null como
    // informação confiável.
    /// <summary>Edição do SO reportada pelo agente (ex.: "Windows Server 2022 Datacenter").</summary>
    public string? OsEdition { get; set; }

    /// <summary>
    /// true quando o SO é uma edição de servidor (Windows Server). Derivado no
    /// agente; no servidor é aceito como booleano ou como texto
    /// ("true"/"false"/"1"/"0") para tolerar payloads de diferentes versões.
    /// </summary>
    public bool? IsServer { get; set; }

    /// <summary>
    /// ProductType bruto do Windows (1 = Workstation, 2 = DomainController,
    /// 3 = Server/NT). Guardado como texto para não perder o valor original
    /// nem quebrar quando o agente enviar rótulos em vez de números.
    /// </summary>
    public string? ProductType { get; set; }

    /// <summary>Domínio AD ao qual o host está associado (null quando fora de domínio).</summary>
    public string? Domain { get; set; }

    /// <summary>Grupo de trabalho do host (null quando o host está em domínio).</summary>
    public string? Workgroup { get; set; }

    /// <summary>true quando o host é membro de um domínio AD (PartOfDomain).</summary>
    public bool? PartOfDomain { get; set; }

    public DateTime CollectedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
