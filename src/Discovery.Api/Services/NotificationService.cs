using System.Text.Json;
using Discovery.Api.Hubs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Services;

public class NotificationService : INotificationService
{
    private readonly INotificationRepository _repository;
    private readonly IHubContext<NotificationHub> _notificationHubContext;
    private readonly IHubContext<AgentHub> _agentHubContext;

    public NotificationService(
        INotificationRepository repository,
        IHubContext<NotificationHub> notificationHubContext,
        IHubContext<AgentHub> agentHubContext)
    {
        _repository = repository;
        _notificationHubContext = notificationHubContext;
        _agentHubContext = agentHubContext;
    }

    public async Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
    {
        var notification = new AppNotification
        {
            EventType = request.EventType,
            Topic = request.Topic,
            Severity = request.Severity,
            RecipientUserId = request.RecipientUserId,
            RecipientAgentId = request.RecipientAgentId,
            RecipientKey = request.RecipientKey,
            Title = request.Title,
            Message = request.Message,
            PayloadJson = request.Payload is null ? null : JsonSerializer.Serialize(request.Payload),
            IsRead = false,
            CreatedBy = request.CreatedBy
        };

        var created = await _repository.CreateAsync(notification);

        var dto = new
        {
            created.Id,
            created.EventType,
            created.Topic,
            created.Severity,
            created.RecipientUserId,
            created.RecipientAgentId,
            created.RecipientKey,
            created.Title,
            created.Message,
            created.PayloadJson,
            created.IsRead,
            created.CreatedAt,
            created.ReadAt,
            created.CreatedBy
        };

        await _notificationHubContext.Clients.Group("notifications:all").SendAsync("NotificationReceived", dto, cancellationToken);
        await _notificationHubContext.Clients.Group($"notifications:topic:{created.Topic}").SendAsync("NotificationReceived", dto, cancellationToken);

        if (created.RecipientUserId.HasValue)
        {
            await _notificationHubContext.Clients.Group($"notifications:user:{created.RecipientUserId}")
                .SendAsync("NotificationReceived", dto, cancellationToken);
        }

        if (created.RecipientAgentId.HasValue)
        {
            await _notificationHubContext.Clients.Group($"notifications:agent:{created.RecipientAgentId}")
                .SendAsync("NotificationReceived", dto, cancellationToken);

            // Repassa diretamente para o canal dos agentes conectados no AgentHub.
            await _agentHubContext.Clients.Group($"agent-{created.RecipientAgentId}")
                .SendAsync("NotificationReceived", dto, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(created.RecipientKey))
        {
            await _notificationHubContext.Clients.Group($"notifications:key:{created.RecipientKey}")
                .SendAsync("NotificationReceived", dto, cancellationToken);
        }

        return created;
    }

    public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
        => _repository.GetRecentAsync(recipientUserId, recipientAgentId, recipientKey, topic, severity, isRead, limit);

    public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
        => _repository.MarkAsReadAsync(id, recipientUserId, recipientAgentId, recipientKey);
}
