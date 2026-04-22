namespace Discovery.Core.Entities;

public class WingetPackage
{
    public Guid Id { get; set; }

    /// <summary>Package identifier (e.g. "Microsoft.PowerToys"). Unique, case-insensitive.</summary>
    public string PackageId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string InstallCommand { get; set; } = string.Empty;

    /// <summary>Tags stored as space-separated values.</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>Serialized JSON object with architecture -> installer URL map.</summary>
    public string InstallerUrlsJson { get; set; } = "{}";

    public DateTime? LastUpdated { get; set; }
    public DateTime? SourceGeneratedAt { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
