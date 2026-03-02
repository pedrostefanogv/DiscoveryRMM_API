using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/clients/{clientId:guid}/[controller]")]
public class SitesController : ControllerBase
{
    private readonly ISiteRepository _repo;

    public SitesController(ISiteRepository repo) => _repo = repo;

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
            Address = request.Address,
            City = request.City,
            State = request.State,
            ZipCode = request.ZipCode,
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
        site.Address = request.Address;
        site.City = request.City;
        site.State = request.State;
        site.ZipCode = request.ZipCode;
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
}

public record CreateSiteRequest(string Name, string? Address, string? City, string? State, string? ZipCode, string? Notes);
public record UpdateSiteRequest(string Name, string? Address, string? City, string? State, string? ZipCode, string? Notes, bool IsActive);
