using Discovery.Core.Cqrs.Mfa.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/mfa")]
public class MfaController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetKeys([FromQuery] Guid userId)
    {
        var result = await mediator.Send(new ListMfaKeysQuery(userId));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
