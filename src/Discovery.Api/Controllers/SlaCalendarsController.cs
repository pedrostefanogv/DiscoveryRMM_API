using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Gerencia calendários de horas úteis para cálculo de SLA.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/sla-calendars")]
public class SlaCalendarsController : ControllerBase
{
    private readonly ISlaCalendarRepository _calendarRepo;

    public SlaCalendarsController(ISlaCalendarRepository calendarRepo)
        => _calendarRepo = calendarRepo;

    /// <summary>
    /// Lista todos os calendários (opcionalmente filtrado por clientId).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? clientId, CancellationToken ct)
    {
        var calendars = await _calendarRepo.GetAllAsync(clientId, ct);
        return Ok(calendars.Select(c => new
        {
            c.Id,
            c.Name,
            c.ClientId,
            c.Timezone,
            c.WorkDayStartHour,
            c.WorkDayEndHour,
            c.WorkDaysJson,
            HolidayCount = c.Holidays.Count
        }));
    }

    /// <summary>
    /// Obtém um calendário pelo Id, incluindo feriados.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var calendar = await _calendarRepo.GetByIdAsync(id, ct);
        if (calendar is null)
            return NotFound(new { error = "Calendário não encontrado." });

        return Ok(new
        {
            calendar.Id,
            calendar.Name,
            calendar.ClientId,
            calendar.Timezone,
            calendar.WorkDayStartHour,
            calendar.WorkDayEndHour,
            calendar.WorkDaysJson,
            Holidays = calendar.Holidays.Select(h => new { h.Id, h.Date, h.Name })
        });
    }

    /// <summary>
    /// Cria um novo calendário de horas úteis.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSlaCalendarRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name é obrigatório." });

        var calendar = await _calendarRepo.CreateAsync(new SlaCalendar
        {
            Name = request.Name,
            ClientId = request.ClientId,
            Timezone = request.Timezone ?? "UTC",
            WorkDayStartHour = request.WorkDayStartHour ?? 8,
            WorkDayEndHour = request.WorkDayEndHour ?? 18,
            WorkDaysJson = request.WorkDaysJson ?? "[1,2,3,4,5]"
        }, ct);

        return CreatedAtAction(nameof(Get), new { id = calendar.Id }, new { calendar.Id });
    }

    /// <summary>
    /// Atualiza configurações do calendário.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSlaCalendarRequest request, CancellationToken ct)
    {
        var calendar = await _calendarRepo.GetByIdAsync(id, ct);
        if (calendar is null)
            return NotFound(new { error = "Calendário não encontrado." });

        if (!string.IsNullOrWhiteSpace(request.Name)) calendar.Name = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Timezone)) calendar.Timezone = request.Timezone;
        if (request.WorkDayStartHour.HasValue) calendar.WorkDayStartHour = request.WorkDayStartHour.Value;
        if (request.WorkDayEndHour.HasValue) calendar.WorkDayEndHour = request.WorkDayEndHour.Value;
        if (!string.IsNullOrWhiteSpace(request.WorkDaysJson)) calendar.WorkDaysJson = request.WorkDaysJson;

        await _calendarRepo.UpdateAsync(calendar, ct);
        return Ok(new { calendar.Id, calendar.Name });
    }

    /// <summary>
    /// Remove um calendário.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _calendarRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Adiciona um feriado ao calendário.
    /// </summary>
    [HttpPost("{id:guid}/holidays")]
    public async Task<IActionResult> AddHoliday(Guid id, [FromBody] AddHolidayRequest request, CancellationToken ct)
    {
        var calendar = await _calendarRepo.GetByIdAsync(id, ct);
        if (calendar is null)
            return NotFound(new { error = "Calendário não encontrado." });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name é obrigatório." });

        var holiday = await _calendarRepo.AddHolidayAsync(new SlaCalendarHoliday
        {
            CalendarId = id,
            Date = request.Date,
            Name = request.Name
        }, ct);

        return Ok(new { holiday.Id, holiday.Date, holiday.Name });
    }

    /// <summary>
    /// Remove um feriado do calendário.
    /// </summary>
    [HttpDelete("{id:guid}/holidays/{holidayId:guid}")]
    public async Task<IActionResult> DeleteHoliday(Guid id, Guid holidayId, CancellationToken ct)
    {
        await _calendarRepo.DeleteHolidayAsync(holidayId, ct);
        return NoContent();
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────

public record CreateSlaCalendarRequest(
    string Name,
    Guid? ClientId,
    string? Timezone,
    int? WorkDayStartHour,
    int? WorkDayEndHour,
    string? WorkDaysJson);

public record UpdateSlaCalendarRequest(
    string? Name,
    string? Timezone,
    int? WorkDayStartHour,
    int? WorkDayEndHour,
    string? WorkDaysJson);

public record AddHolidayRequest(
    DateTime Date,
    string Name);
