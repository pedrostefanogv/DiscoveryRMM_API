using Discovery.Core.Cqrs.AutomationScripts.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/automation/scripts")]
public class AutomationScriptsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? clientId = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListAutomationScriptsQuery(clientId, cursor, limit));
        return result.ToActionResult();
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetAutomationScriptByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
