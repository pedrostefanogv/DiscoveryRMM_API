namespace Discovery.Core.Entities;

/// <summary>
/// Modelo de TRANSPORTE do feed OData do Chocolatey, consumido por
/// <c>ChocolateyApiClient</c> e convertido em <see cref="AppPackage"/> pelo
/// <c>AppCatalogSyncService</c>. NÃO é mapeado no EF: a tabela legada
/// chocolatey_packages foi removida pela M055 (mapeamento excluído na limpeza
/// pós-incidente de 2026-10-31).
/// </summary>
public class ChocolateyPackage
{
    public Guid Id { get; set; }

    /// <summary>Package identifier (e.g. "googlechrome"). Unique, case-insensitive.</summary>
    public string PackageId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string LicenseUrl { get; set; } = string.Empty;

    /// <summary>Space-separated tags as returned by the OData feed (e.g. "browser chrome admin").</summary>
    public string Tags { get; set; } = string.Empty;

    public long DownloadCount { get; set; }
    public DateTime? LastUpdated { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
