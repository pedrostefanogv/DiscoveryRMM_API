using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-saved-views")]
public class TicketSavedViewsController : ControllerBase
{
    private readonly ITicketSavedViewRepository _repo;

    public TicketSavedViewsController(ITicketSavedViewRepository repo)
    {
        _repo = repo;
    }

    /// <summary>
    /// Lista visões salvas de um usuário (inclui compartilhadas).
    /// </summary>
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] Guid? userId)
    {
        var views = await _repo.GetByUserAsync(userId);
        return Ok(views);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var view = await _repo.GetByIdAsync(id);
        return view is null ? NotFound() : Ok(view);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateTicketSavedViewRequest request)
    {
        var filterJson = JsonSerializer.Serialize(request.Filter ?? new TicketFilterQuery());

        var view = new TicketSavedView
        {
            UserId = request.UserId,
            Name = request.Name,
            FilterJson = filterJson,
            IsShared = request.IsShared
        };

        var created = await _repo.CreateAsync(view);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketSavedViewRequest request)
    {
        var view = await _repo.GetByIdAsync(id);
        if (view is null) return NotFound();

        view.Name = request.Name;
        view.FilterJson = JsonSerializer.Serialize(request.Filter ?? new TicketFilterQuery());
        view.IsShared = request.IsShared;

        await _repo.UpdateAsync(view);
        return Ok(view);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var view = await _repo.GetByIdAsync(id);
        if (view is null) return NotFound();

        await _repo.DeleteAsync(id);
        return NoContent();
    }
}
