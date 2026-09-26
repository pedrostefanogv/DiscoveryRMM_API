using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.Support.Departments;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Cqrs.Departments.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

public record AddDepartmentMemberRequest(Guid UserId);

public record DepartmentCustomFieldRequest(
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    bool? IsRequired,
    bool? IsInternal,
    bool? IsActive,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    string? InputMask,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue);

[ApiController]
[Route("api/v{version:apiVersion}/departments")]
public class DepartmentsController(
    IMediator mediator,
    ICustomFieldService customFieldService,
    IDepartmentCustomFieldService departmentCustomFieldService) : ControllerBase
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
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateDepartmentCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
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
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteDepartmentCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Members (round-robin) ────────────────────────────────────────────

    [HttpGet("{id:guid}/members")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetMembers(Guid id)
        => (await mediator.Send(new ListDepartmentMembersQuery(id), HttpContext.RequestAborted)).ToActionResult();

    [HttpPost("{id:guid}/members")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddDepartmentMemberRequest request)
        => (await mediator.Send(new AddDepartmentMemberCommand(id, request.UserId), HttpContext.RequestAborted)).ToActionResult();

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
        => (await mediator.Send(new RemoveDepartmentMemberCommand(id, userId), HttpContext.RequestAborted)).ToActionResult();

    // ── Custom Fields ────────────────────────────────────────────────────

    // Lista as DEFINIÇÕES de campos do departamento (não os valores). A tela de
    // configuração do departamento espera id/dataType/isInternal/optionsJson para
    // editar/excluir; devolver valores deixava o id vazio e o PUT ia para
    // ".../custom-fields//definition" (404).
    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetCustomFields(Guid id)
        => Ok(await departmentCustomFieldService.GetDefinitionsByDepartmentAsync(id, HttpContext.RequestAborted));

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomField(Guid id, Guid definitionId, [FromBody] JsonElement body)
    {
        var valueJson = body.TryGetProperty("value", out var prop) ? prop.GetRawText() : body.GetRawText();
        var result = await customFieldService.UpsertValueAsync(
            new UpsertCustomFieldValueInput(definitionId, CustomFieldScopeType.Department, id, valueJson, Username),
            HttpContext.RequestAborted);
        return Ok(result);
    }

    // ── Custom Field Definitions (formulário de abertura) ────────────────

    [HttpPost("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> CreateCustomFieldDefinition(Guid id, [FromBody] DepartmentCustomFieldRequest request)
    {
        try
        {
            var created = await departmentCustomFieldService.CreateDepartmentFieldAsync(
                id, ToCreateInput(request), Username, HttpContext.RequestAborted);
            return CreatedAtAction(nameof(GetTicketSchema), new { id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    [HttpPut("{id:guid}/custom-fields/{fieldId:guid}/definition")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> UpdateCustomFieldDefinition(Guid id, Guid fieldId, [FromBody] DepartmentCustomFieldRequest request)
    {
        try
        {
            var updated = await departmentCustomFieldService.UpdateDepartmentFieldAsync(
                fieldId, id, ToUpdateInput(request), Username, HttpContext.RequestAborted);
            if (updated is null)
                return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Campo não encontrado." } } });
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    [HttpDelete("{id:guid}/custom-fields/{fieldId:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> DeleteCustomFieldDefinition(Guid id, Guid fieldId)
    {
        try
        {
            var removed = await departmentCustomFieldService.DeleteDepartmentFieldAsync(
                fieldId, id, HttpContext.RequestAborted);
            return removed ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    [HttpGet("{id:guid}/ticket-schema")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetTicketSchema(Guid id)
        => Ok(await departmentCustomFieldService.GetPublicSchemaForDepartmentAsync(id, HttpContext.RequestAborted));

    private static CreateDepartmentCustomFieldInput ToCreateInput(DepartmentCustomFieldRequest r) => new(
        r.Name, r.Label, r.Description, r.DataType,
        r.IsRequired ?? false, r.IsInternal ?? false, r.IsActive ?? true,
        r.Options, r.ValidationRegex, r.InputMask,
        r.MinLength, r.MaxLength, r.MinValue, r.MaxValue);

    private static UpdateDepartmentCustomFieldInput ToUpdateInput(DepartmentCustomFieldRequest r) => new(
        r.Name, r.Label, r.Description, r.DataType,
        r.IsRequired ?? false, r.IsInternal ?? false, r.IsActive ?? true,
        r.Options, r.ValidationRegex, r.InputMask,
        r.MinLength, r.MaxLength, r.MinValue, r.MaxValue);
}
