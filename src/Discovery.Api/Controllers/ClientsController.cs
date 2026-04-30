using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class ClientsController : ControllerBase
{
    private readonly IClientRepository _repo;
    private readonly ICustomFieldService _customFieldService;

    public ClientsController(IClientRepository repo, ICustomFieldService customFieldService)
    {
        _repo = repo;
        _customFieldService = customFieldService;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] bool includeInactive = false)
    {
        var clients = await _repo.GetAllAsync(includeInactive);
        return Ok(clients);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var client = await _repo.GetByIdAsync(id);
        return client is null ? NotFound() : Ok(client);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Clients, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateClientRequest request)
    {
        var client = new Client
        {
            Name = request.Name,
            Notes = request.Notes
        };
        var created = await _repo.CreateAsync(client);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClientRequest request)
    {
        var client = await _repo.GetByIdAsync(id);
        if (client is null) return NotFound();

        client.Name = request.Name;
        client.Notes = request.Notes;
        client.IsActive = request.IsActive;

        await _repo.UpdateAsync(client);
        return Ok(client);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _repo.DeleteAsync(id);
        return NoContent();
    }

    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetCustomFieldValues(Guid id, [FromQuery] bool includeSecrets = true, CancellationToken cancellationToken = default)
    {
        var client = await _repo.GetByIdAsync(id);
        if (client is null)
            return NotFound();

        var values = await _customFieldService.GetValuesAsync(CustomFieldScopeType.Client, id, includeSecrets, cancellationToken);
        return Ok(values);
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomFieldValue(
        Guid id,
        Guid definitionId,
        [FromBody] UpsertClientCustomFieldValueRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = await _repo.GetByIdAsync(id);
        if (client is null)
            return NotFound();

        try
        {
            var value = await _customFieldService.UpsertValueAsync(
                new Discovery.Core.DTOs.UpsertCustomFieldValueInput(
                    definitionId,
                    CustomFieldScopeType.Client,
                    id,
                    request.Value.GetRawText(),
                    HttpContext.Items["Username"] as string ?? "api"),
                cancellationToken);

            return Ok(value);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public record CreateClientRequest(string Name, string? Notes);
public record UpdateClientRequest(string Name, string? Notes, bool IsActive);
public record UpsertClientCustomFieldValueRequest(System.Text.Json.JsonElement Value);
