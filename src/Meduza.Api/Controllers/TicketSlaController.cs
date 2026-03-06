using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/tickets/{ticketId:guid}/sla")]
public class TicketSlaController : ControllerBase
{
    private readonly ITicketRepository _ticketRepo;
    private readonly ISlaService _slaService;

    public TicketSlaController(
        ITicketRepository ticketRepo,
        ISlaService slaService)
    {
        _ticketRepo = ticketRepo;
        _slaService = slaService;
    }

    /// <summary>
    /// Obtém o status do SLA do ticket.
    /// Retorna: horasRestantes, percentualUsado, expirado
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetSlaStatus(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        if (!ticket.SlaExpiresAt.HasValue)
            return Ok(new { message = "Este ticket não possui SLA configurado" });

        var (hoursRemaining, percentUsed, breached) = await _slaService.GetSlaStatusAsync(ticketId);

        return Ok(new
        {
            SlaExpiresAt = ticket.SlaExpiresAt,
            HoursRemaining = hoursRemaining,
            PercentUsed = Math.Round(percentUsed, 2),
            Breached = breached,
            Status = breached ? "🔴 VIOLADO" : 
                     percentUsed >= 80 ? "🟠 CRÍTICO (>80%)" :
                     percentUsed >= 50 ? "🟡 ATENÇÃO (>50%)" : "🟢 OK"
        });
    }

    /// <summary>
    /// Obtém informações detalhadas de SLA do ticket.
    /// </summary>
    [HttpGet("details")]
    public async Task<IActionResult> GetSlaDetails(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        if (!ticket.SlaExpiresAt.HasValue)
            return Ok(new { message = "Este ticket não possui SLA configurado" });

        var (hoursRemaining, percentUsed, breached) = await _slaService.GetSlaStatusAsync(ticketId);
        var now = DateTime.UtcNow;
        var totalSlaHours = (int)(ticket.SlaExpiresAt.Value - ticket.CreatedAt).TotalHours;
        var elapsedHours = (int)(now - ticket.CreatedAt).TotalHours;

        var details = new
        {
            Ticket = new { ticket.Id, ticket.Title },
            CreatedAt = ticket.CreatedAt,
            SlaExpiresAt = ticket.SlaExpiresAt,
            TotalSlaHours = totalSlaHours,
            ElapsedHours = elapsedHours,
            HoursRemaining = hoursRemaining,
            PercentUsed = Math.Round(percentUsed, 2),
            Breached = ticket.SlaBreached,
            Status = ticket.SlaBreached ? "VIOLADO" : 
                     percentUsed >= 80 ? "CRÍTICO" :
                     percentUsed >= 50 ? "ATENÇÃO" : "OK",
            WarningLevel = percentUsed switch
            {
                < 50 => "low",
                < 80 => "medium",
                < 100 => "high",
                _ => "critical"
            }
        };

        return Ok(details);
    }
}
