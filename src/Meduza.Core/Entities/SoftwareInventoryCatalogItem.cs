namespace Meduza.Core.Entities;

public class SoftwareInventoryCatalogItem
{
    public Guid SoftwareId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Source { get; set; }
    public int InstalledCount { get; set; }
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastCollectedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}