namespace Discovery.Core.Entities;

/// <summary>
/// Role/feature do Windows instalada no host (ex.: AD DS, DNS, Hyper-V, File
/// Server). Só é coletada em Windows Server — estações devolvem lista vazia.
/// </summary>
public class AgentWindowsRole
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>Nome técnico da role (ex.: "AD-Domain-Services").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Nome amigável exibido no site (ex.: "Active Directory Domain Services").</summary>
    public string? DisplayName { get; set; }

    /// <summary>true quando a role está instalada no host.</summary>
    public bool Installed { get; set; }

    /// <summary>Estado bruto de instalação reportado pelo Windows (ex.: "Installed").</summary>
    public string? InstallState { get; set; }

    public DateTime CollectedAt { get; set; }
}
