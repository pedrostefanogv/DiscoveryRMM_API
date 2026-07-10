using System.Text.Json;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.Departments.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/departments")]
public class DepartmentsController(
    IMediator mediator,
    ICustomFieldService customFieldService) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool includeGlobal = true)
    {
        var result = await mediator.Send(new ListDepartmentsQuery(clientId, includeGlobal));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetDepartmentByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDepartmentCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDepartmentCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteDepartmentCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Custom Fields ────────────────────────────────────────────────────

    [HttpGet("{id:guid}/custom-fields")]
    public async Task<IActionResult> GetCustomFields(Guid id, [FromQuery] bool includeSecrets = false)
        => Ok(await customFieldService.GetValuesAsync(CustomFieldScopeType.Department, id, includeSecrets, HttpContext.RequestAborted));

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    public async Task<IActionResult> UpsertCustomField(Guid id, Guid definitionId, [FromBody] JsonElement body)
    {
        var valueJson = body.TryGetProperty("value", out var prop) ? prop.GetRawText() : body.GetRawText();
        var result = await customFieldService.UpsertValueAsync(
            new UpsertCustomFieldValueInput(definitionId, CustomFieldScopeType.Department, id, valueJson, Username),
            HttpContext.RequestAborted);
        return Ok(result);
    }
}
