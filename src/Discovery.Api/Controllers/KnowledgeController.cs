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
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] string? category = null)
    {
        // Listagem unificada: sempre usa ACL multi-escopo do usuário.
        // clientId/siteId refinam o resultado (ex.: dropdown de cliente na UI).
        var result = await mediator.Send(new ListKnowledgeArticlesByUserScopeQuery(
            cursor, limit, status, departmentId, category, clientId, siteId));
        return result.ToActionResult();
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

    // ── Sub-páginas internas do artigo (estilo Notion) ──────────────

    [HttpGet("{id:guid}/pages")]
    public async Task<IActionResult> GetPages(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetArticlePagesQuery(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("{id:guid}/pages/{pageId:guid}")]
    public async Task<IActionResult> GetPage(Guid id, Guid pageId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetArticlePageQuery(id, pageId), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("{id:guid}/pages")]
    public async Task<IActionResult> CreatePage(Guid id, [FromBody] CreateArticlePageRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateArticlePageCommand(
            id, request.Title, request.Content, request.ParentPageId, request.SortOrder), ct);
        return result.Match<IActionResult>(
            success: page => CreatedAtAction(nameof(GetPage), new { id, pageId = page.Id, version = "v1" }, page),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("{id:guid}/pages/{pageId:guid}")]
    public async Task<IActionResult> UpdatePage(Guid id, Guid pageId, [FromBody] UpdateArticlePageRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdateArticlePageCommand(
            id, pageId, request.Title, request.Content, request.ParentPageId, request.SortOrder), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("{id:guid}/pages/{pageId:guid}")]
    public async Task<IActionResult> DeletePage(Guid id, Guid pageId, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteArticlePageCommand(id, pageId), ct);
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
