namespace Discovery.Core.Entities;

public class PrinterInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DriverName { get; set; }
    public string? PortName { get; set; }
    public string? PrinterStatus { get; set; }
    public bool IsDefault { get; set; }
    public bool IsNetworkPrinter { get; set; }
    public bool Shared { get; set; }
    public string? ShareName { get; set; }
    public string? Location { get; set; }
    public DateTime CollectedAt { get; set; }
}