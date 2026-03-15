namespace Meduza.Core.DTOs;

public sealed record DashboardEventMessage(
    string EventType,
    object? Data,
    DateTime TimestampUtc,
    Guid? ClientId,
    Guid? SiteId)
{
    public static DashboardEventMessage Create(string eventType, object? data, Guid? clientId, Guid? siteId)
        => new(eventType, data, DateTime.UtcNow, clientId, siteId);
}
