using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Gerencia seguidores (watchers) de um ticket.
/// Watchers recebem notificações de comentários públicos, mudança de estado e encerramento.
/// </summary>
[ApiController]
[Route("api/tickets/{ticketId:guid}/watchers")]
public class TicketWatchersController : ControllerBase
{
    private readonly ITicketWatcherRepository _watcherRepo;
    private readonly ITicketRepository _ticketRepo;

    public TicketWatchersController(
        ITicketWatcherRepository watcherRepo,
        ITicketRepository ticketRepo)
    {
        _watcherRepo = watcherRepo;
        _ticketRepo = ticketRepo;
    }

    /// <summary>Lista todos os watchers do ticket.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var watchers = await _watcherRepo.GetByTicketAsync(ticketId);
        return Ok(watchers.Select(w => new
        {
            w.Id,
            w.TicketId,
            w.UserId,
            w.AddedBy,
            w.AddedAt
        }));
    }

    /// <summary>Adiciona um watcher ao ticket.</summary>
    [HttpPost]
    public async Task<IActionResult> Add(Guid ticketId, [FromBody] AddTicketWatcherRequest req)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var existing = await _watcherRepo.GetAsync(ticketId, req.UserId);
        if (existing is not null)
            return Conflict(new { error = "Usuário já é watcher deste ticket." });

        var addedBy = User.Identity?.Name;
        var watcher = await _watcherRepo.AddAsync(ticketId, req.UserId, addedBy);

        return CreatedAtAction(
            nameof(List),
            new { ticketId },
            new { watcher.Id, watcher.TicketId, watcher.UserId, watcher.AddedBy, watcher.AddedAt });
    }

    /// <summary>Remove um watcher do ticket.</summary>
    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Remove(Guid ticketId, Guid userId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var existing = await _watcherRepo.GetAsync(ticketId, userId);
        if (existing is null)
            return NotFound(new { error = "Watcher não encontrado." });

        await _watcherRepo.RemoveAsync(ticketId, userId);
        return NoContent();
    }
}
