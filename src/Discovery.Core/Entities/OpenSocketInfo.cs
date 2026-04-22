namespace Discovery.Core.Entities;

public class OpenSocketInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string? ProcessName { get; set; }
    public int ProcessId { get; set; }
    public string? ProcessPath { get; set; }
    public string? LocalAddress { get; set; }
    public int LocalPort { get; set; }
    public string? RemoteAddress { get; set; }
    public int RemotePort { get; set; }
    public string? Protocol { get; set; }
    public string? Family { get; set; }
    public DateTime CollectedAt { get; set; }
}
