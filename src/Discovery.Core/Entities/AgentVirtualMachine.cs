namespace Discovery.Core.Entities;

/// <summary>
/// Máquina virtual do Hyper-V hospedada neste servidor. Vazio quando o host não
/// tem a role Hyper-V habilitada.
/// </summary>
public class AgentVirtualMachine
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Estado estável já normalizado pelo agente (ex.: "Running", "Off").</summary>
    public string? State { get; set; }

    /// <summary>Código bruto do estado CIM (ex.: 2 = Running).</summary>
    public int? StateCode { get; set; }

    public long? MemoryAssignedMb { get; set; }
    public long? UptimeSeconds { get; set; }

    /// <summary>Geração da VM (1 = BIOS legado, 2 = UEFI).</summary>
    public int? Generation { get; set; }

    /// <summary>Versão dos Integration Services reportada pelo host.</summary>
    public string? IntegrationServicesVersion { get; set; }

    public DateTime CollectedAt { get; set; }
}
