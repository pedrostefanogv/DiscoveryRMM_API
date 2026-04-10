using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface INotificationService
{
    Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50);
    Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null);
}

public record NotificationPublishRequest(
    string EventType,
    string Topic,
    string Title,
    string Message,
    NotificationSeverity Severity = NotificationSeverity.Informational,
    object? Payload = null,
    Guid? RecipientUserId = null,
    Guid? RecipientAgentId = null,
    string? RecipientKey = null,
    string? CreatedBy = null);
