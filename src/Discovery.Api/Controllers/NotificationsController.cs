using Discovery.Core.Cqrs.Notifications.Commands;
using Discovery.Core.Cqrs.Notifications.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/notifications")]
public class NotificationsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
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

    [HttpPut("{id:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id, [FromQuery] Guid? userId = null)
    {
        var result = await mediator.Send(new MarkNotificationReadCommand(id, userId));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
