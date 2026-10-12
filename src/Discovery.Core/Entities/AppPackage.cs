using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AppPackage
{
    public Guid Id { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;    public string? Source => InstallationType.ToString();
    public string? Category => null;
    public bool IsActive => true;    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public string? SiteUrl { get; set; }
    public string? InstallCommand { get; set; }

    /// <summary>Comando opcional para remover o pacote custom (executado pelo agent).</summary>
    public string? UninstallCommand { get; set; }

    /// <summary>Indica que o arquivo enviado e um ZIP e deve ser extraido no host antes de instalar.</summary>
    public bool ExtractZip { get; set; }

    /// <summary>Nome original do instalador (para download e logging no agent).</summary>
    public string? FileName { get; set; }

    /// <summary>Chave do icone/capa no object storage (servido pela loja).</summary>
    public string? IconObjectKey { get; set; }

    public string? MetadataJson { get; set; }
    public string? FileObjectKey { get; set; }
    public string? FileBucket { get; set; }
    public string? FilePublicUrl { get; set; }
    public string? FileContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileChecksum { get; set; }
    public DateTime? SourceGeneratedAt { get; set; }
    public DateTime? LastUpdated { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
