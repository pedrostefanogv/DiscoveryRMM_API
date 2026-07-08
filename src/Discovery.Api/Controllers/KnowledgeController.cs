using Discovery.Core.Cqrs.Knowledge.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/knowledge")]
public class KnowledgeController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListKnowledgeArticlesQuery(clientId, siteId, cursor, limit));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] int maxResults = 10)
    {
        var result = await mediator.Send(new SearchKnowledgeQuery(q, clientId, siteId, maxResults));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetKnowledgeArticleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
