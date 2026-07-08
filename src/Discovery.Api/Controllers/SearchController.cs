using Discovery.Core.Cqrs.Search.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/[controller]")]
public class SearchController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int maxResults = 10)
    {
        var userId = HttpContext.Items["UserId"] is Guid uid ? uid : Guid.Empty;
        var result = await mediator.Send(new UniversalSearchQuery(userId, q, maxResults));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => Problem(errors[0].Message, statusCode: 400));
    }
}
