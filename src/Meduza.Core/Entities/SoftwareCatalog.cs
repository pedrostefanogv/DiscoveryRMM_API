namespace Meduza.Core.Entities;

public class SoftwareCatalog
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? InstallId { get; set; }
    public string? Serial { get; set; }
    public string? Source { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
