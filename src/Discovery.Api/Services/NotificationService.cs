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
    private readonly IAgentCommandDispatcher _commandDispatcher;
    private readonly IHubContext<NotificationHub> _notificationHubContext;
    private readonly IRedisService _redis;
    private readonly ILogger<NotificationService> _logger;

    private const string BroadcastChannel = "notifications:broadcast";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NotificationService(
        INotificationRepository repository,
        IAgentCommandDispatcher commandDispatcher,
        IHubContext<NotificationHub> notificationHubContext,
        IRedisService redis,
        ILogger<NotificationService> logger)
    {
        _repository = repository;
        _commandDispatcher = commandDispatcher;
        _notificationHubContext = notificationHubContext;
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

        if (created.RecipientAgentId.HasValue)
            await DispatchAgentNotificationCommandAsync(created, cancellationToken);

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
        await SendLocalSignalRAsync(dto, created.Topic, created.RecipientUserId, created.RecipientKey, cancellationToken);

        return created;
    }

    private async Task DispatchAgentNotificationCommandAsync(AppNotification notification, CancellationToken cancellationToken)
    {
        if (!notification.RecipientAgentId.HasValue)
            return;

        object metadata = new Dictionary<string, object?>
        {
            ["topic"] = notification.Topic,
            ["createdBy"] = notification.CreatedBy
        };

        if (!string.IsNullOrWhiteSpace(notification.PayloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(notification.PayloadJson, JsonOptions);
                metadata = new Dictionary<string, object?>
                {
                    ["topic"] = notification.Topic,
                    ["createdBy"] = notification.CreatedBy,
                    ["payload"] = payload
                };
            }
            catch (JsonException)
            {
                metadata = new Dictionary<string, object?>
                {
                    ["topic"] = notification.Topic,
                    ["createdBy"] = notification.CreatedBy,
                    ["payloadRaw"] = notification.PayloadJson
                };
            }
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            notificationId = notification.Id.ToString("N"),
            idempotencyKey = $"{notification.Id:N}:{notification.RecipientAgentId.Value:N}",
            title = notification.Title,
            message = notification.Message,
            mode = string.IsNullOrWhiteSpace(notification.Message) ? "interactive" : "notify_only",
            severity = MapNotificationSeverity(notification.Severity),
            eventType = notification.EventType,
            layout = "toast",
            timeoutSeconds = 8,
            metadata
        }, JsonOptions);

        await _commandDispatcher.DispatchAsync(
            new AgentCommand
            {
                AgentId = notification.RecipientAgentId.Value,
                CommandType = CommandType.Notification,
                Payload = payloadJson
            },
            cancellationToken);
    }

    private static string MapNotificationSeverity(NotificationSeverity severity)
    {
        return severity switch
        {
            NotificationSeverity.Informational => "low",
            NotificationSeverity.Warning => "high",
            NotificationSeverity.Critical => "critical",
            _ => "medium"
        };
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
                string? recipientKey = dto.TryGetProperty("recipientKey", out var rkEl) && rkEl.ValueKind != JsonValueKind.Null
                    ? rkEl.GetString() : null;

                await SendLocalSignalRAsync(dto, topic, recipientUserId, recipientKey, CancellationToken.None);
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
        string? recipientKey,
        CancellationToken cancellationToken)
    {
        await _notificationHubContext.Clients.Group("notifications:all").SendAsync("NotificationReceived", dto, cancellationToken);
        if (!string.IsNullOrWhiteSpace(topic))
            await _notificationHubContext.Clients.Group($"notifications:topic:{topic}").SendAsync("NotificationReceived", dto, cancellationToken);

        if (recipientUserId.HasValue)
            await _notificationHubContext.Clients.Group($"notifications:user:{recipientUserId}")
                .SendAsync("NotificationReceived", dto, cancellationToken);

        if (!string.IsNullOrWhiteSpace(recipientKey))
            await _notificationHubContext.Clients.Group($"notifications:key:{recipientKey}")
                .SendAsync("NotificationReceived", dto, cancellationToken);
    }

    public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
        => _repository.GetRecentAsync(recipientUserId, recipientAgentId, recipientKey, topic, severity, isRead, limit);

    public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
        => _repository.MarkAsReadAsync(id, recipientUserId, recipientAgentId, recipientKey);
}
