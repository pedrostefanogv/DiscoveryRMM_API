using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Notifications.Queries;

public sealed record ListNotificationsQuery(
    Guid? RecipientUserId = null, Guid? RecipientAgentId = null,
    string? Topic = null, bool? IsRead = null, int Limit = 50
) : IQuery<Result<IReadOnlyList<NotificationDto>>>;

public sealed record NotificationDto(
    Guid Id, string EventType, string Topic, string Title,
    string Message, string Severity, bool IsRead, DateTime CreatedAt
);
