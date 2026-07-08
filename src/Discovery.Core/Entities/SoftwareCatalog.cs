namespace Discovery.Core.Entities;

public class SoftwareCatalog
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? InstallId { get; set; }
    public string? Serial { get; set; }
    public string? Source { get; set; }
    public string Fingerprint { get; set; } = string.Empty;

    // ── Report fields ───────────────────────────────────────────────────
    public string? Category { get; set; }
    public string? LatestVersion { get; set; }
    public DateTime? EolDate { get; set; }
    public bool IsEol { get; set; }
    public string? LicenseType { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
