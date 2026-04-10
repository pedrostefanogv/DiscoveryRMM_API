namespace Discovery.Core.Entities;

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
