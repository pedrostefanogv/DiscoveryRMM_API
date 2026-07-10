using Discovery.Core.Cqrs.Mfa.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/mfa")]
public class MfaController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetKeys([FromQuery] Guid userId)
    {
        var result = await mediator.Send(new ListMfaKeysQuery(userId));
        return result.ToActionResult();
    }

    [HttpGet("keys")]
    public async Task<IActionResult> GetMyKeys()
    {
        if (HttpContext.Items["UserId"] is Guid userId)
        {
            var result = await mediator.Send(new ListMfaKeysQuery(userId));
            return result.ToActionResult();
        }
        return Unauthorized(new { error = "Not authenticated." });
    }
}
