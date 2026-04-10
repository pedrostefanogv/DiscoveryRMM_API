using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClientsController : ControllerBase
{
    private readonly IClientRepository _repo;

    public ClientsController(IClientRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool includeInactive = false)
    {
        var clients = await _repo.GetAllAsync(includeInactive);
        return Ok(clients);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var client = await _repo.GetByIdAsync(id);
        return client is null ? NotFound() : Ok(client);
    }

    [HttpPost]
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
    public async Task<IActionResult> Delete(Guid id)
    {
        await _repo.DeleteAsync(id);
        return NoContent();
    }
}

public record CreateClientRequest(string Name, string? Notes);
public record UpdateClientRequest(string Name, string? Notes, bool IsActive);
