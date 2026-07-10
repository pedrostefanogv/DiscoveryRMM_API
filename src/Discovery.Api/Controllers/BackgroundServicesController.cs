using Discovery.Api.Cqrs.BackgroundServices;
using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/admin/background-services")]
public class BackgroundServicesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new ListBackgroundServicesQuery());
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{name}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetByName(string name)
    {
        var result = await mediator.Send(new GetBackgroundServiceByNameQuery(name));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
