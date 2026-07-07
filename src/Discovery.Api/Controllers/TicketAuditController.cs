using Discovery.Api.Filters;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/tickets/{ticketId:guid}/audit")]
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
    /// Timeline unificada: combina atividades e comentários em ordem cronológica, com paginação por cursor.
    /// </summary>
    [HttpGet("timeline/unified")]
    [RequirePermission(ResourceType.Logs, ActionType.View)]
    public async Task<IActionResult> GetUnifiedTimeline(
        Guid ticketId,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound("Ticket não encontrado.");

        var logs = await _logRepo.GetByTicketAsync(ticketId);
        var comments = await _ticketRepo.GetCommentsAsync(ticketId);

        // Criar entries tipadas (sem dynamic)
        var entries = new List<TimelineEntry>();

        foreach (var l in logs)
        {
            entries.Add(new TimelineEntry(
                l.Id, "activity", l.Type.ToString(),
                l.ChangedByUserId?.ToString(),
                l.OldValue, l.NewValue,
                l.Comment ?? GetActivityDescription(l),
                l.CreatedAt));
        }

        foreach (var c in comments)
        {
            entries.Add(new TimelineEntry(
                c.Id, "comment", "Commented",
                c.Author,
                null, null,
                c.IsInternal ? $"[interno] {c.Content}" : c.Content,
                c.CreatedAt));
        }

        // Ordenar cronologicamente
        var ordered = entries
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToList();

        // Paginação por cursor
        var safeLimit = Math.Clamp(limit, 1, 200);
        var items = ordered.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(cursor)
            && Discovery.Core.Helpers.CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            items = items.Where(e =>
                e.CreatedAt < cursorCreatedAtUtc
                || (e.CreatedAt == cursorCreatedAtUtc && e.Id.CompareTo(cursorId) < 0));
        }

        var page = items.Take(safeLimit).ToList();
        var hasMore = ordered.Count > page.Count + (string.IsNullOrWhiteSpace(cursor) ? 0 : ordered.Count - items.Count());
        var nextCursor = hasMore && page.Count > 0
            ? Discovery.Core.Helpers.CursorPaginationHelper.EncodeCreatedAtCursor(page[^1].CreatedAt, page[^1].Id)
            : null;

        return Ok(new
        {
            items = page,
            cursor = nextCursor,
            hasMore,
            limit = safeLimit
        });
    }

    /// <summary>
    /// Obtém o histórico completo (timeline) de um ticket.
    /// </summary>
    [HttpGet("timeline")]
    [RequirePermission(ResourceType.Logs, ActionType.View)]
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
    [RequirePermission(ResourceType.Logs, ActionType.View)]
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
    [RequirePermission(ResourceType.Logs, ActionType.View)]
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
    [RequirePermission(ResourceType.Logs, ActionType.View)]
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
    [RequirePermission(ResourceType.Logs, ActionType.View)]
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
    [RequirePermission(ResourceType.Logs, ActionType.View)]
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

    private static string GetActivityDescription(TicketActivityLog log)
    {
        return log.Type switch
        {
            TicketActivityType.Created => "Ticket criado",
            TicketActivityType.StateChanged => $"Estado alterado de {log.OldValue} para {log.NewValue}",
            TicketActivityType.Assigned => $"Atribuído para {log.NewValue}",
            TicketActivityType.PriorityChanged => $"Prioridade alterada de {log.OldValue} para {log.NewValue}",
            TicketActivityType.SlaBreached => "SLA violado",
            TicketActivityType.SlaWarning => "Aviso de SLA",
            TicketActivityType.Escalated => "Ticket escalado",
            TicketActivityType.Reopened => "Ticket reaberto",
            _ => log.Type.ToString()
        };
    }
}

/// <summary>
/// Entrada tipada da timeline unificada (substitui uso de dynamic).
/// </summary>
public sealed record TimelineEntry(
    Guid Id,
    string Kind,
    string TypeLabel,
    string? Actor,
    string? OldValue,
    string? NewValue,
    string? Detail,
    DateTime CreatedAt);
