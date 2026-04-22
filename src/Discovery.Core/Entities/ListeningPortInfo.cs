namespace Discovery.Core.Entities;

public class ListeningPortInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string? ProcessName { get; set; }
    public int ProcessId { get; set; }
    public string? ProcessPath { get; set; }
    public string? Protocol { get; set; }
    public string? Address { get; set; }
    public int Port { get; set; }
    public DateTime CollectedAt { get; set; }
}
