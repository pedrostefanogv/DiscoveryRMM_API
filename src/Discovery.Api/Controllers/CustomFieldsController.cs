using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/custom-fields")]
public class CustomFieldsController : ControllerBase
{
    private readonly ICustomFieldService _service;

    public CustomFieldsController(ICustomFieldService service)
    {
        _service = service;
    }

    [HttpGet("definitions")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = "Medium")]
    public async Task<IActionResult> GetDefinitions(
        [FromQuery] CustomFieldScopeType? scopeType = null,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var items = await _service.GetDefinitionsAsync(scopeType, includeInactive, cancellationToken);
        return Ok(items);
    }

    [HttpGet("definitions/{id:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetDefinitionById(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await _service.GetDefinitionByIdAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("definitions")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Create)]
    public async Task<IActionResult> CreateDefinition(
        [FromBody] UpsertCustomFieldDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var created = await _service.CreateDefinitionAsync(
                request.ToInput(),
                HttpContext.Items["Username"] as string ?? "api",
                cancellationToken);
            return CreatedAtAction(nameof(GetDefinitionById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("definitions/{id:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateDefinition(
        Guid id,
        [FromBody] UpsertCustomFieldDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await _service.UpdateDefinitionAsync(
                id,
                request.ToInput(),
                HttpContext.Items["Username"] as string ?? "api",
                cancellationToken);

            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("definitions/{id:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteDefinition(Guid id, CancellationToken cancellationToken = default)
    {
        var removed = await _service.DeactivateDefinitionAsync(id, cancellationToken);
        return removed ? NoContent() : NotFound();
    }

    [HttpGet("values/{scopeType}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetValues(
        CustomFieldScopeType scopeType,
        [FromQuery] Guid? entityId = null,
        [FromQuery] bool includeSecrets = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var values = await _service.GetValuesAsync(scopeType, entityId, includeSecrets, cancellationToken);
            return Ok(values);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("schema/{scopeType}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetSchema(
        CustomFieldScopeType scopeType,
        [FromQuery] Guid? entityId = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool includeSecrets = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var schema = await _service.GetSchemaAsync(scopeType, entityId, includeInactive, includeSecrets, cancellationToken);
            return Ok(schema);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("values/{definitionId:guid}")]
    public async Task<IActionResult> UpsertValue(
        Guid definitionId,
        [FromBody] UpsertCustomFieldValueRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.UpsertValueAsync(
                new UpsertCustomFieldValueInput(
                    definitionId,
                    request.ScopeType,
                    request.EntityId,
                    request.Value.GetRawText(),
                    HttpContext.Items["Username"] as string ?? "api"),
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed record UpsertCustomFieldDefinitionRequest(
    string Name,
    string Label,
    string? Description,
    CustomFieldScopeType ScopeType,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsActive,
    bool IsSecret,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    bool AllowRuntimeRead,
    bool AllowAgentWrite,
    CustomFieldRuntimeAccessMode RuntimeAccessMode,
    IReadOnlyList<CustomFieldAccessBindingRequest>? AccessBindings)
{
    public UpsertCustomFieldDefinitionInput ToInput()
    {
        return new UpsertCustomFieldDefinitionInput(
            Name,
            Label,
            Description,
            ScopeType,
            DataType,
            IsRequired,
            IsActive,
            IsSecret,
            Options,
            ValidationRegex,
            MinLength,
            MaxLength,
            MinValue,
            MaxValue,
            AllowRuntimeRead,
            AllowAgentWrite,
            RuntimeAccessMode,
            AccessBindings?.Select(binding => new CustomFieldAccessBindingInput(binding.TaskId, binding.ScriptId, binding.CanRead, binding.CanWrite)).ToList());
    }
}

public sealed record CustomFieldAccessBindingRequest(
    Guid? TaskId,
    Guid? ScriptId,
    bool CanRead,
    bool CanWrite);

public sealed record UpsertCustomFieldValueRequest(
    CustomFieldScopeType ScopeType,
    Guid? EntityId,
    System.Text.Json.JsonElement Value);
