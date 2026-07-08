using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Notifications.Commands;
using Discovery.Core.Cqrs.Notifications.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Notifications;

public sealed class ListNotificationsQueryHandler(
    INotificationService service
) : IRequestHandler<ListNotificationsQuery, Result<IReadOnlyList<NotificationDto>>>
{
    public async Task<Result<IReadOnlyList<NotificationDto>>> Handle(
        ListNotificationsQuery q, CancellationToken ct)
    {
        var notifications = await service.GetRecentAsync(
            q.RecipientUserId, q.RecipientAgentId, null, q.Topic, null, q.IsRead, q.Limit);
        var items = notifications.Select(n => new NotificationDto(
            n.Id, n.EventType, n.Topic, n.Title, n.Message,
            n.Severity.ToString(), n.IsRead, n.CreatedAt)).ToList().AsReadOnly();
        return Result<IReadOnlyList<NotificationDto>>.Success(items);
    }
}

public sealed class MarkNotificationReadCommandHandler(
    INotificationService service
) : IRequestHandler<MarkNotificationReadCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(MarkNotificationReadCommand cmd, CancellationToken ct)
    {
        var ok = await service.MarkAsReadAsync(cmd.Id, cmd.RecipientUserId);
        return ok
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Notification {cmd.Id} not found"));
    }
}
