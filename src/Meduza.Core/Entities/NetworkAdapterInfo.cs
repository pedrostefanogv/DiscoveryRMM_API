namespace Meduza.Core.Entities;

public class NetworkAdapterInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? MacAddress { get; set; }
    public string? IpAddress { get; set; }
    public string? SubnetMask { get; set; }
    public string? Gateway { get; set; }
    public string? DnsServers { get; set; }
    public bool IsDhcpEnabled { get; set; }
    public string? AdapterType { get; set; }
    public string? Speed { get; set; }
    public DateTime CollectedAt { get; set; }
}
