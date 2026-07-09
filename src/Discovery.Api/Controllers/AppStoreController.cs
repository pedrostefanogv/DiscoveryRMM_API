using Discovery.Core.Cqrs.AppStore.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/app-store")]
public class AppStoreController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? search = null, [FromQuery] string? architecture = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new SearchAppStoreQuery(search, architecture, cursor, limit));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> GetCatalog([FromQuery] int installationType = 0, [FromQuery] string? search = null, [FromQuery] string? architecture = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new GetAppStoreCatalogQuery(installationType, search, architecture, cursor, limit));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("effective")]
    public async Task<IActionResult> GetEffective([FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] Guid? agentId = null)
    {
        var result = await mediator.Send(new GetAppStoreEffectiveAppsQuery(clientId, siteId, agentId));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
