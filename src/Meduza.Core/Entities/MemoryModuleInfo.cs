namespace Meduza.Core.Entities;

/// <summary>
/// Informação de módulo de memória RAM individual do agent.
/// </summary>
public class MemoryModuleInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string? Slot { get; set; }
    public long CapacityBytes { get; set; }
    public int? SpeedMhz { get; set; }
    public string? MemoryType { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public string? SerialNumber { get; set; }
    public DateTime CollectedAt { get; set; }
}
