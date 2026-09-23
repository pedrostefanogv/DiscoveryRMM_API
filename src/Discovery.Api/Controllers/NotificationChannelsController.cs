using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Support.Channels;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/notification-channels")]
public class NotificationChannelsController(IMediator mediator) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [HttpGet]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAll()
        => (await mediator.Send(new ListNotificationChannelsQuery(), HttpContext.RequestAborted)).ToActionResult();

    [HttpPost]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateNotificationChannelCommand cmd)
        => (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateNotificationChannelCommand cmd)
        => (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id)
        => (await mediator.Send(new DeleteNotificationChannelCommand(id), HttpContext.RequestAborted)).ToActionResult();
}
