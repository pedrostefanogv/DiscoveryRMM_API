using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/clients/{clientId:guid}/[controller]")]
public class SitesController : ControllerBase
{
    private readonly ISiteRepository _repo;
    private readonly ICustomFieldService _customFieldService;

    public SitesController(ISiteRepository repo, ICustomFieldService customFieldService)
    {
        _repo = repo;
        _customFieldService = customFieldService;
    }

    [HttpGet]
    public async Task<IActionResult> GetByClient(Guid clientId, [FromQuery] bool includeInactive = false)
    {
        var sites = await _repo.GetByClientIdAsync(clientId, includeInactive);
        return Ok(sites);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid clientId, Guid id)
    {
        var site = await _repo.GetByIdAsync(id);
        if (site is null || site.ClientId != clientId) return NotFound();
        return Ok(site);
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid clientId, [FromBody] CreateSiteRequest request)
    {
        var site = new Site
        {
            ClientId = clientId,
            Name = request.Name,
            Notes = request.Notes
        };
        var created = await _repo.CreateAsync(site);
        return CreatedAtAction(nameof(GetById), new { clientId, id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid clientId, Guid id, [FromBody] UpdateSiteRequest request)
    {
        var site = await _repo.GetByIdAsync(id);
        if (site is null || site.ClientId != clientId) return NotFound();

        site.Name = request.Name;
        site.Notes = request.Notes;
        site.IsActive = request.IsActive;

        await _repo.UpdateAsync(site);
        return Ok(site);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid clientId, Guid id)
    {
        var site = await _repo.GetByIdAsync(id);
        if (site is null || site.ClientId != clientId) return NotFound();
        await _repo.DeleteAsync(id);
        return NoContent();
    }

    [HttpGet("{id:guid}/custom-fields")]
    public async Task<IActionResult> GetCustomFieldValues(
        Guid clientId,
        Guid id,
        [FromQuery] bool includeSecrets = true,
        CancellationToken cancellationToken = default)
    {
        var site = await _repo.GetByIdAsync(id);
        if (site is null || site.ClientId != clientId)
            return NotFound();

        var values = await _customFieldService.GetValuesAsync(CustomFieldScopeType.Site, id, includeSecrets, cancellationToken);
        return Ok(values);
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    public async Task<IActionResult> UpsertCustomFieldValue(
        Guid clientId,
        Guid id,
        Guid definitionId,
        [FromBody] UpsertSiteCustomFieldValueRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await _repo.GetByIdAsync(id);
        if (site is null || site.ClientId != clientId)
            return NotFound();

        try
        {
            var value = await _customFieldService.UpsertValueAsync(
                new Discovery.Core.DTOs.UpsertCustomFieldValueInput(
                    definitionId,
                    CustomFieldScopeType.Site,
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

public record CreateSiteRequest(string Name, string? Notes);
public record UpdateSiteRequest(string Name, string? Notes, bool IsActive);
public record UpsertSiteCustomFieldValueRequest(System.Text.Json.JsonElement Value);
