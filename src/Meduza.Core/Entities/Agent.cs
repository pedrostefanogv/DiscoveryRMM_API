using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class Agent
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Offline;
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
    public string? LastIpAddress { get; set; }
    public string? MacAddress { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Indica que o agent foi registrado via zero-touch (discovery) e aguarda aprovação manual.</summary>
    public bool ZeroTouchPending { get; set; }
}
