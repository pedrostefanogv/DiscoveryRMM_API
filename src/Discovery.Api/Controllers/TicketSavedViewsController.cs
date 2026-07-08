using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-saved-views")]
public class TicketSavedViewsController(ITicketSavedViewRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = HttpContext.Items["UserId"] as Guid?;
        var views = await repo.GetByUserAsync(userId);
        return Ok(views);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var view = await repo.GetByIdAsync(id);
        if (view is null) return NotFound();
        return Ok(view);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketSavedViewRequest request)
    {
        var userId = HttpContext.Items["UserId"] as Guid?;
        var view = new TicketSavedView
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name,
            FilterJson = request.FilterJson ?? "{}",
            IsShared = request.IsShared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var created = await repo.CreateAsync(view);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketSavedViewRequest request)
    {
        var existing = await repo.GetByIdAsync(id);
        if (existing is null) return NotFound();
        if (request.Name is not null) existing.Name = request.Name;
        if (request.FilterJson is not null) existing.FilterJson = request.FilterJson;
        if (request.IsShared.HasValue) existing.IsShared = request.IsShared.Value;
        existing.UpdatedAt = DateTime.UtcNow;
        await repo.UpdateAsync(existing);
        return Ok(existing);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await repo.DeleteAsync(id);
        return NoContent();
    }
}

public record CreateTicketSavedViewRequest(string Name, string? FilterJson, bool IsShared = false);
public record UpdateTicketSavedViewRequest(string? Name, string? FilterJson, bool? IsShared);
