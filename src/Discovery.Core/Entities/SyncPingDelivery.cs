using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class SyncPingDelivery
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid AgentId { get; set; }
    public SyncResourceType Resource { get; set; }
    public string Revision { get; set; } = string.Empty;
    public SyncPingDeliveryStatus Status { get; set; } = SyncPingDeliveryStatus.Sent;
    public DateTime SentAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AckMetadataJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
