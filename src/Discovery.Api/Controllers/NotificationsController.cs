using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Notifications.Commands;
using Discovery.Core.Cqrs.Notifications.Queries;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/notifications")]
public class NotificationsController(IMediator mediator) : ControllerBase
{
    /// <summary>List notifications with optional filters.</summary>
    [HttpGet]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? recipientUserId = null,
        [FromQuery] Guid? recipientAgentId = null,
        [FromQuery] string? topic = null,
        [FromQuery] bool? isRead = null,
        [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListNotificationsQuery(
            recipientUserId, recipientAgentId, topic, isRead, limit));
        return result.ToActionResult();
    }

    /// <summary>Mark a single notification as read.</summary>
    [HttpPut("{id:guid}/read")]
    [RequirePermission(ResourceType.Dashboard, ActionType.Edit)]
    public async Task<IActionResult> MarkAsRead(
        Guid id,
        [FromQuery] Guid? userId = null,
        [FromQuery] Guid? agentId = null)
    {
        var result = await mediator.Send(new MarkNotificationReadCommand(id, userId, agentId));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Delete a single notification by ID.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Dashboard, ActionType.Delete)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] Guid? userId = null,
        [FromQuery] Guid? agentId = null)
    {
        var result = await mediator.Send(new DeleteNotificationCommand(id, userId, agentId));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
