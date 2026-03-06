using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/tickets/{ticketId:guid}/audit")]
public class TicketAuditController : ControllerBase
{
    private readonly ITicketActivityLogRepository _logRepo;
    private readonly ITicketRepository _ticketRepo;

    public TicketAuditController(
        ITicketActivityLogRepository logRepo,
        ITicketRepository ticketRepo)
    {
        _logRepo = logRepo;
        _ticketRepo = ticketRepo;
    }

    /// <summary>
    /// Obtém o histórico completo (timeline) de um ticket.
    /// </summary>
    [HttpGet("timeline")]
    public async Task<IActionResult> GetTimeline(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        var logs = await _logRepo.GetByTicketAsync(ticketId);
        return Ok(logs);
    }

    /// <summary>
    /// Obtém atividades filtrando por tipo.
    /// </summary>
    [HttpGet("timeline/activity-type/{activityType}")]
    public async Task<IActionResult> GetByActivityType(Guid ticketId, string activityType)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        if (!Enum.TryParse<TicketActivityType>(activityType, true, out var type))
            return BadRequest($"Tipo de atividade inválido: {activityType}");

        var logs = await _logRepo.GetByTypeAsync(ticketId, type);
        return Ok(logs);
    }

    /// <summary>
    /// Obtém atividades filtrando por usuário.
    /// </summary>
    [HttpGet("timeline/user/{userId:guid}")]
    public async Task<IActionResult> GetByUser(Guid ticketId, Guid userId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        var logs = await _logRepo.GetByUserAsync(ticketId, userId);
        return Ok(logs);
    }

    /// <summary>
    /// Obtém histórico filtrado por data.
    /// </summary>
    [HttpGet("timeline/date-range")]
    public async Task<IActionResult> GetByDateRange(
        Guid ticketId,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        if (from > to)
            return BadRequest("Data 'from' não pode ser maior que 'to'.");

        var logs = await _logRepo.GetByDateRangeAsync(ticketId, from, to);
        return Ok(logs);
    }

    /// <summary>
    /// Obtém a última atividade registrada no ticket.
    /// </summary>
    [HttpGet("timeline/last")]
    public async Task<IActionResult> GetLastActivity(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        var lastLog = await _logRepo.GetLastActivityAsync(ticketId);
        return lastLog is null ? NotFound() : Ok(lastLog);
    }

    /// <summary>
    /// Obtém estatísticas de atividade do ticket.
    /// </summary>
    [HttpGet("statistics")]
    public async Task<IActionResult> GetStatistics(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        var totalActivities = await _logRepo.GetActivityCountAsync(ticketId);
        var timeline = await _logRepo.GetByTicketAsync(ticketId);

        var stateChanges = timeline.Where(l => l.Type == TicketActivityType.StateChanged).Count();
        var assignments = timeline.Where(l => l.Type == TicketActivityType.Assigned).Count();
        var comments = timeline.Where(l => l.Type == TicketActivityType.Commented).Count();
        var slaEvents = timeline.Where(l => l.Type == TicketActivityType.SlaBreached || l.Type == TicketActivityType.SlaWarning).Count();

        var stats = new
        {
            TotalActivities = totalActivities,
            StateChanges = stateChanges,
            Assignments = assignments,
            Comments = comments,
            SlaEvents = slaEvents,
            FirstActivity = timeline.FirstOrDefault()?.CreatedAt,
            LastActivity = timeline.LastOrDefault()?.CreatedAt,
            DaysOpen = ticket.DaysOpen
        };

        return Ok(stats);
    }
}
