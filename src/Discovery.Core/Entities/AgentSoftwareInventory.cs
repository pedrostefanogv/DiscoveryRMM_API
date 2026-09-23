namespace Discovery.Core.Entities;

public class AgentSoftwareInventory
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid SoftwareId { get; set; }
    public DateTime CollectedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? Version { get; set; }
    public DateTime? InstallDate { get; set; }
    public string? InstallSource { get; set; }
    /// <summary>Versão disponível para atualização (winget/chocolatey), quando há update pendente.</summary>
    public string? AvailableVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    /// <summary>Gerenciador de pacotes do update: "winget" ou "chocolatey".</summary>
    public string? UpdateSource { get; set; }
    /// <summary>Identificador do pacote no gerenciador (winget Id / choco Id) para executar o update.</summary>
    public string? UpdatePackageId { get; set; }
    public bool IsPresent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}