namespace Discovery.Core.Entities;

public class AlertCorrelationLock
{
    public string DedupKey { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public Guid? LastTicketId { get; set; }
    public DateTime LastAlertAt { get; set; }
}