using System.Text.Json.Serialization;
using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class Agent
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? MeshCentralNodeId { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Offline;
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
    public string? LastIpAddress { get; set; }
    public string? MacAddress { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool ZeroTouchPending { get; set; }

    public bool MaintenanceEnabled { get; set; }
    public string? MaintenanceReason { get; set; }
    public DateTime? MaintenanceChangedAt { get; set; }
    public Guid? MaintenanceChangedByUserId { get; set; }

    [JsonIgnore]
    public AgentStatus EffectiveStatus => MaintenanceEnabled ? AgentStatus.Maintenance : Status;
}
