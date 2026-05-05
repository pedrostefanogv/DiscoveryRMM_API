using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

public class NotificationService : INotificationService
{
    private readonly INotificationRepository _repository;
    private readonly IAgentCommandDispatcher _commandDispatcher;
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
        IRedisService redis,
        ILogger<NotificationService> logger)
    {
        _repository = repository;
        _commandDispatcher = commandDispatcher;
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

        // Broadcast via Redis Pub/Sub for multi-instance consumers.
        await BroadcastViaRedisAsync(dto);

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

    public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
        => _repository.GetRecentAsync(recipientUserId, recipientAgentId, recipientKey, topic, severity, isRead, limit);

    public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
        => _repository.MarkAsReadAsync(id, recipientUserId, recipientAgentId, recipientKey);
}
