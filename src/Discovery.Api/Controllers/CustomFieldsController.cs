using Discovery.Core.Cqrs.CustomFields.Commands;
using Discovery.Core.Cqrs.CustomFields.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/custom-fields")]
public class CustomFieldsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? scopeType = null, [FromQuery] bool includeInactive = false)
    {
        var result = await mediator.Send(new ListCustomFieldsQuery(scopeType, includeInactive));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetCustomFieldByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("values/{scopeType}")]
    public async Task<IActionResult> GetValues(
        string scopeType,
        [FromQuery] Guid? entityId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        [FromQuery] bool includeSecrets = true)
    {
        var result = await mediator.Send(new ListCustomFieldValuesQuery(scopeType, entityId, cursor, limit, includeSecrets));
        return result.ToActionResult();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomFieldCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCustomFieldCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("values/{definitionId:guid}")]
    public async Task<IActionResult> UpsertValue(Guid definitionId, [FromBody] UpsertCustomFieldValueCommand cmd)
    {
        var result = await mediator.Send(cmd with { DefinitionId = definitionId });
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var result = await mediator.Send(new DeactivateCustomFieldCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
