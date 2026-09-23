namespace Discovery.Core.Entities;

public class SoftwareInventoryEntry
{
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallId { get; set; }
    public string? Serial { get; set; }
    public string? Source { get; set; }
    public DateTime? InstallDate { get; set; }
    public string? InstallSource { get; set; }
    /// <summary>Versão disponível para atualização (winget/chocolatey), quando há update pendente.</summary>
    public string? AvailableVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    /// <summary>Gerenciador de pacotes do update: "winget" ou "chocolatey".</summary>
    public string? UpdateSource { get; set; }
    /// <summary>Identificador do pacote no gerenciador (winget Id / choco Id) para executar o update.</summary>
    public string? UpdatePackageId { get; set; }
}
