namespace Discovery.Core.Entities;

/// <summary>
/// Entrada de CATÁLOGO de aplicativos de terceiros (WU-21, decisão D24).
///
/// O catálogo é a curadoria sobre os manifestos do `winget-pkgs` (ou de um MSI
/// corporativo): fixa versão + hash + URL + switches silenciosos + ProductCode.
/// Regras que justificam a tabela:
///  • o agente baixa do FORNECEDOR e valida o hash — o binário nunca é
///    redistribuído pelo produto;
///  • app de terceiros só é AUTO-aprovado se estiver aqui com `Status=active`:
///    o catálogo é a barreira contra instalar qualquer coisa que o gerenciador
///    de pacotes ofereça;
///  • revogar uma versão (CVE, regressão) tem efeito imediato na aprovação.
///
/// Uma linha por (source, packageId, version).
/// </summary>
public class AppCatalogEntry
{
    public Guid Id { get; set; }

    /// <summary>winget | msi (a origem precisa bater com a do inventário).</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>ID do pacote no provedor (ex.: Google.Chrome) — casa o inventário.</summary>
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    /// <summary>Versão curada (string do provedor).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>URL oficial do fornecedor (baixada na hora da instalação).</summary>
    public string InstallerUrl { get; set; } = string.Empty;
    /// <summary>SHA-256 (hex) do instalador — verificado antes de executar.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>msi | msix | exe | inno | nsis | wix | burn | null.</summary>
    public string? InstallerType { get; set; }
    /// <summary>Switches silenciosos validados em VM (ex.: "/S").</summary>
    public string? SilentArgs { get; set; }
    /// <summary>ProductCode/UpgradeCode do MSI, quando aplicável.</summary>
    public string? ProductCode { get; set; }

    /// <summary>active | revoked.</summary>
    public string Status { get; set; } = "active";
    public DateTime? ReleaseDate { get; set; }
    public string? Notes { get; set; }

    public string? CuratedBy { get; set; }
    public DateTime CuratedAt { get; set; } = DateTime.UtcNow;
    public string? RevokedBy { get; set; }
    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
