using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Discovery.Core.Enums;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<IActionResult> GetRecent(
        [FromQuery] Guid? recipientUserId,
        [FromQuery] Guid? recipientAgentId,
        [FromQuery] string? recipientKey,
        [FromQuery] string? topic,
        [FromQuery] NotificationSeverity? severity,
        [FromQuery] bool? isRead,
        [FromQuery] int limit = 50)
    {
        var notifications = await _notificationService.GetRecentAsync(recipientUserId, recipientAgentId, recipientKey, topic, severity, isRead, limit);
        return Ok(notifications);
    }

    [HttpPost]
    public async Task<IActionResult> Publish([FromBody] PublishNotificationRequest request, CancellationToken cancellationToken)
    {
        var notification = await _notificationService.PublishAsync(new NotificationPublishRequest(
            EventType: request.EventType,
            Topic: request.Topic,
            Title: request.Title,
            Message: request.Message,
            Severity: request.Severity,
            Payload: request.Payload,
            RecipientUserId: request.RecipientUserId,
            RecipientAgentId: request.RecipientAgentId,
            RecipientKey: request.RecipientKey,
            CreatedBy: request.CreatedBy),
            cancellationToken);

        return CreatedAtAction(nameof(GetRecent), new { notification.Id }, notification);
    }

    [HttpPatch("{id:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id, [FromQuery] Guid? recipientUserId, [FromQuery] Guid? recipientAgentId, [FromQuery] string? recipientKey)
    {
        var marked = await _notificationService.MarkAsReadAsync(id, recipientUserId, recipientAgentId, recipientKey);
        return marked ? NoContent() : NotFound();
    }
}

public record PublishNotificationRequest(
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
