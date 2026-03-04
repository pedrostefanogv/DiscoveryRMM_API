namespace Meduza.Core.Entities;

public class SoftwareInventoryEntry
{
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallId { get; set; }
    public string? Serial { get; set; }
    public string? Source { get; set; }
}
