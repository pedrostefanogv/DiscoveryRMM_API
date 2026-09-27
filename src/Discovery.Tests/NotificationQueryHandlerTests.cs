using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Cqrs.Notifications.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Notifications;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class NotificationQueryHandlerTests
{
    [Test]
    public async Task ListNotifications_ShouldPreservePayloadJson()
    {
        var payloadJson = "{\"ticketId\":\"01a0de51-f18c-7050-9063-0a22ea412020\",\"ticketTitle\":\"Notebook não liga\"}";
        var notification = new AppNotification
        {
            Id = Guid.NewGuid(),
            EventType = "ticket.assigned",
            Topic = "tickets",
            Title = "Chamado atribuído",
            Message = "Chamado \"Notebook não liga\" atribuído a você.",
            Severity = NotificationSeverity.Informational,
            PayloadJson = payloadJson,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        var handler = new ListNotificationsQueryHandler(new FakeNotificationService(notification));

        var result = await handler.Handle(new ListNotificationsQuery(), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Has.Count.EqualTo(1));
        var dto = result.Value![0];
        Assert.That(dto.EventType, Is.EqualTo("ticket.assigned"));
        Assert.That(dto.PayloadJson, Is.EqualTo(payloadJson));
    }

    [Test]
    public async Task ListNotifications_ShouldKeepNullPayloadJson()
    {
        var notification = new AppNotification
        {
            Id = Guid.NewGuid(),
            EventType = "report.generated",
            Topic = "reports",
            Title = "Relatório",
            Message = "Relatório gerado.",
            Severity = NotificationSeverity.Informational,
            PayloadJson = null,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        var handler = new ListNotificationsQueryHandler(new FakeNotificationService(notification));

        var result = await handler.Handle(new ListNotificationsQuery(), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Has.Count.EqualTo(1));
        Assert.That(result.Value![0].PayloadJson, Is.Null);
    }

    private sealed class FakeNotificationService : INotificationService
    {
        private readonly IReadOnlyList<AppNotification> _items;

        public FakeNotificationService(params AppNotification[] items) => _items = items;

        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AppNotification
            {
                Id = Guid.NewGuid(),
                EventType = request.EventType,
                Topic = request.Topic,
                Title = request.Title,
                Message = request.Message,
                Severity = request.Severity,
                PayloadJson = request.Payload is null ? null : System.Text.Json.JsonSerializer.Serialize(request.Payload),
                CreatedAt = DateTime.UtcNow
            });

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult(_items);

        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(true);

        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(true);
    }
}
