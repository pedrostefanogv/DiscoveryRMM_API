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
    private readonly IRedisService _redis;
    private readonly ILogger<NotificationService> _logger;

    private const string BroadcastChannel = "notifications:broadcast";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NotificationService(
        INotificationRepository repository,
        IHubContext<NotificationHub> notificationHubContext,
        IHubContext<AgentHub> agentHubContext,
        IRedisService redis,
        ILogger<NotificationService> logger)
    {
        _repository = repository;
        _notificationHubContext = notificationHubContext;
        _agentHubContext = agentHubContext;
        _redis = redis;
        _logger = logger;
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

        // Broadcast via Redis Pub/Sub for multi-instance
        await BroadcastViaRedisAsync(dto);

        // Send locally via SignalR
        await SendLocalSignalRAsync(dto, created.Topic, created.RecipientUserId, created.RecipientAgentId, created.RecipientKey, cancellationToken);

        return created;
    }

    /// <summary>
    /// Broadcasts notification to all API instances via Redis Pub/Sub.
    /// Each instance picks up the message and relays to its local SignalR clients.
    /// </summary>
    private async Task BroadcastViaRedisAsync(object dto)
    {
        if (!_redis.IsConnected) return;

        try
        {
            var message = JsonSerializer.Serialize(dto, JsonOptions);
            await _redis.PublishAsync(BroadcastChannel, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast notification via Redis");
        }
    }

    /// <summary>
    /// Receives notification from Redis broadcast and relays to local SignalR clients.
    /// </summary>
    public async Task StartRedisListenerAsync(CancellationToken cancellationToken)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("Redis not connected — notification broadcast listener disabled");
            return;
        }

        await _redis.SubscribeAsync(BroadcastChannel, async (channel, message) =>
        {
            try
            {
                var dto = JsonSerializer.Deserialize<JsonElement>(message, JsonOptions);
                if (dto.ValueKind != JsonValueKind.Object) return;

                // Only relay from other instances (local delivery already handled in PublishAsync)
                var topic = dto.TryGetProperty("topic", out var topicEl)
                    ? topicEl.GetString() : null;
                Guid? recipientUserId = dto.TryGetProperty("recipientUserId", out var uidEl) && uidEl.ValueKind != JsonValueKind.Null
                    ? uidEl.GetGuid() : null;
                Guid? recipientAgentId = dto.TryGetProperty("recipientAgentId", out var aidEl) && aidEl.ValueKind != JsonValueKind.Null
                    ? aidEl.GetGuid() : null;
                string? recipientKey = dto.TryGetProperty("recipientKey", out var rkEl) && rkEl.ValueKind != JsonValueKind.Null
                    ? rkEl.GetString() : null;

                await SendLocalSignalRAsync(dto, topic, recipientUserId, recipientAgentId, recipientKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error relaying Redis broadcast notification");
            }
        });
    }

    private async Task SendLocalSignalRAsync(
        object dto,
        string? topic,
        Guid? recipientUserId,
        Guid? recipientAgentId,
        string? recipientKey,
        CancellationToken cancellationToken)
    {
        await _notificationHubContext.Clients.Group("notifications:all").SendAsync("NotificationReceived", dto, cancellationToken);
        if (!string.IsNullOrWhiteSpace(topic))
            await _notificationHubContext.Clients.Group($"notifications:topic:{topic}").SendAsync("NotificationReceived", dto, cancellationToken);

        if (recipientUserId.HasValue)
            await _notificationHubContext.Clients.Group($"notifications:user:{recipientUserId}")
                .SendAsync("NotificationReceived", dto, cancellationToken);

        if (recipientAgentId.HasValue)
        {
            await _notificationHubContext.Clients.Group($"notifications:agent:{recipientAgentId}")
                .SendAsync("NotificationReceived", dto, cancellationToken);

            await _agentHubContext.Clients.Group($"agent-{recipientAgentId}")
                .SendAsync("NotificationReceived", dto, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(recipientKey))
            await _notificationHubContext.Clients.Group($"notifications:key:{recipientKey}")
                .SendAsync("NotificationReceived", dto, cancellationToken);
    }

    public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
        => _repository.GetRecentAsync(recipientUserId, recipientAgentId, recipientKey, topic, severity, isRead, limit);

    public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
        => _repository.MarkAsReadAsync(id, recipientUserId, recipientAgentId, recipientKey);
}
