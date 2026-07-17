using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.Cqrs.Knowledge.Queries;
using Discovery.Core.DTOs;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/knowledge")]
public class KnowledgeController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string? scopeMode = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] string? category = null)
    {
        // scopeMode=all-visible: usa ACL do usuário + cursor pagination → ArticleListPage
        if (string.Equals(scopeMode, "all-visible", StringComparison.OrdinalIgnoreCase))
        {
            var result = await mediator.Send(new ListKnowledgeArticlesByUserScopeQuery(cursor, limit, status, departmentId, category));
            return result.ToActionResult();
        }

        // Legacy: escopo fixo clientId/siteId → IReadOnlyList<ArticleResponse>
        var legacyResult = await mediator.Send(new ListKnowledgeArticlesQuery(clientId, siteId, cursor, limit));
        return legacyResult.ToActionResult();
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] int maxResults = 10)
    {
        var result = await mediator.Send(new SearchKnowledgeQuery(q, clientId, siteId, maxResults));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetKnowledgeArticleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateArticleRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateKnowledgeArticleCommand(
            request.Title, request.Content, request.Category, request.Tags,
            request.CreatedBy, request.ClientId, request.SiteId, request.DepartmentId), ct);
        return result.Match<IActionResult>(
            success: article => CreatedAtAction(nameof(GetById), new { id = article.Id, version = "v1" }, article),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateArticleRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdateKnowledgeArticleCommand(
            id, request.Title, request.Content, request.Category, request.Tags, request.LastEditedBy), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteKnowledgeArticleCommand(id), ct);
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, [FromBody] PublishArticleRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new PublishKnowledgeArticleCommand(id, request.Status, request.LastEditedBy, request.ChangeSummary), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("{id:guid}/unpublish")]
    public async Task<IActionResult> Unpublish(Guid id, [FromQuery] string? lastEditedBy = null, CancellationToken ct = default)
    {
        var result = await mediator.Send(new UnpublishKnowledgeArticleCommand(id, lastEditedBy), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> GetVersions(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetKnowledgeArticleVersionsQuery(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
