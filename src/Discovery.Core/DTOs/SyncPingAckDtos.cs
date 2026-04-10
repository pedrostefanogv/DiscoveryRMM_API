using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class SyncPingAckRequest
{
    public string Revision { get; set; } = string.Empty;
    public SyncResourceType Resource { get; set; }
    public string Status { get; set; } = "success";
    public DateTime? ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? AckMetadataJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SyncPingAckResponse
{
    public bool Acknowledged { get; set; }
    public Guid EventId { get; set; }
    public Guid DeliveryId { get; set; }
}
