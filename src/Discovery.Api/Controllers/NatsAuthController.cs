using Discovery.Core.Cqrs.NatsAuth.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/nats-auth")]
public class NatsAuthController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        var result = await mediator.Send(new GetNatsStatusQuery());
        return result.ToActionResult();
    }
}
