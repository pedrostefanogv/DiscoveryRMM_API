namespace Meduza.Core.Entities;

public class SoftwareInventoryTopItem
{
    public Guid SoftwareId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Source { get; set; }
    public int InstalledCount { get; set; }
}
