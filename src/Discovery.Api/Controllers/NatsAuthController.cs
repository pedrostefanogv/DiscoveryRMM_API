using Discovery.Core.Cqrs.NatsAuth.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/nats-auth")]
public class NatsAuthController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        var result = await mediator.Send(new GetNatsStatusQuery());
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
