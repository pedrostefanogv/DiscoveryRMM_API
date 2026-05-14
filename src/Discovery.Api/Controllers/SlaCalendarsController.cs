using Discovery.Api.Filters;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
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
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
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
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
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
            Holidays = calendar.Holidays.Select(h => new
            {
                h.Id,
                h.Date,
                h.Name,
                holidayType = h.HolidayTypeValue,
                relativeMonth = h.RelativeMonth,
                relativeDayOfWeek = h.RelativeDayOfWeek,
                relativeOccurrence = h.RelativeOccurrence,
                relativeMethod = h.RelativeMethodValue,
            })
        });
    }

    /// <summary>
    /// Cria um novo calendário de horas úteis.
    /// </summary>
    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
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
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
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
    [RequirePermission(ResourceType.Tickets, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _calendarRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Adiciona um feriado ao calendário.
    /// holidayType: 0=Fixed, 1=Yearly, 2=Relative.
    /// Para Relative, informe relativeMonth (1-12), relativeDayOfWeek (0=Dom..6=Sab),
    /// relativeOccurrence (1-5, 5=ultima), relativeMethod (0=DayOfWeekOccurrence, 1=NthBusinessDay).
    /// </summary>
    [HttpPost("{id:guid}/holidays")]
    [RequirePermission(ResourceType.Tickets, ActionType.Create)]
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
            Name = request.Name,
            HolidayTypeValue = request.HolidayType,
            RelativeMonth = request.RelativeMonth,
            RelativeDayOfWeek = request.RelativeDayOfWeek,
            RelativeOccurrence = request.RelativeOccurrence,
            RelativeMethodValue = request.RelativeMethod,
        }, ct);

        return Ok(new
        {
            holiday.Id,
            holiday.Date,
            holiday.Name,
            holidayType = holiday.HolidayTypeValue,
            relativeMonth = holiday.RelativeMonth,
            relativeDayOfWeek = holiday.RelativeDayOfWeek,
            relativeOccurrence = holiday.RelativeOccurrence,
            relativeMethod = holiday.RelativeMethodValue,
        });
    }

    /// <summary>
    /// Atualiza um feriado existente.
    /// </summary>
    [HttpPut("{id:guid}/holidays/{holidayId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> UpdateHoliday(Guid id, Guid holidayId, [FromBody] AddHolidayRequest request, CancellationToken ct)
    {
        var calendar = await _calendarRepo.GetByIdAsync(id, ct);
        if (calendar is null)
            return NotFound(new { error = "Calendário não encontrado." });

        var holiday = calendar.Holidays.FirstOrDefault(h => h.Id == holidayId);
        if (holiday is null)
            return NotFound(new { error = "Feriado não encontrado." });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name é obrigatório." });

        holiday.Name = request.Name;
        holiday.Date = request.Date;
        holiday.HolidayTypeValue = request.HolidayType;
        holiday.RelativeMonth = request.RelativeMonth;
        holiday.RelativeDayOfWeek = request.RelativeDayOfWeek;
        holiday.RelativeOccurrence = request.RelativeOccurrence;
        holiday.RelativeMethodValue = request.RelativeMethod;

        await _calendarRepo.UpdateHolidayAsync(holiday, ct);

        return Ok(new
        {
            holiday.Id,
            holiday.Date,
            holiday.Name,
            holidayType = holiday.HolidayTypeValue,
            relativeMonth = holiday.RelativeMonth,
            relativeDayOfWeek = holiday.RelativeDayOfWeek,
            relativeOccurrence = holiday.RelativeOccurrence,
            relativeMethod = holiday.RelativeMethodValue,
        });
    }

    /// <summary>
    /// Remove um feriado do calendário.
    /// </summary>
    [HttpDelete("{id:guid}/holidays/{holidayId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Delete)]
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
    string Name,
    int HolidayType = 0,
    int? RelativeMonth = null,
    int? RelativeDayOfWeek = null,
    int? RelativeOccurrence = null,
    int? RelativeMethod = null);
