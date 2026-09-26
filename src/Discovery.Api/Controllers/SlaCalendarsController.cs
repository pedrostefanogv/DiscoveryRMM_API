using Discovery.Api.Filters;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.SlaCalendars.Commands;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Cqrs.SlaCalendars.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/sla-calendars")]
public class SlaCalendarsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Sla, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new ListSlaCalendarsQuery(clientId));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetSlaCalendarByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: NotFoundOrBadRequest);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Sla, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateSlaCalendarCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: BadRequestWithFields);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSlaCalendarCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: NotFoundOrBadRequest);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteSlaCalendarCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: ConflictAwareFailure);
    }

    // ── Feriados ──────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/holidays")]
    [RequirePermission(ResourceType.Sla, ActionType.View)]
    public async Task<IActionResult> GetHolidays(Guid id)
    {
        var result = await mediator.Send(new GetSlaCalendarByIdQuery(id));
        return result.Match<IActionResult>(
            success: dto => Ok(dto.Holidays),
            failure: NotFoundOrBadRequest);
    }

    [HttpPost("{id:guid}/holidays")]
    [RequirePermission(ResourceType.Sla, ActionType.Edit)]
    public async Task<IActionResult> AddHoliday(Guid id, [FromBody] AddSlaCalendarHolidayCommand cmd)
    {
        var result = await mediator.Send(cmd with { CalendarId = id });
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetHolidays), new { id }, dto),
            failure: ConflictAwareFailure);
    }

    [HttpPut("{id:guid}/holidays/{holidayId:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.Edit)]
    public async Task<IActionResult> UpdateHoliday(Guid id, Guid holidayId, [FromBody] UpdateSlaCalendarHolidayCommand cmd)
    {
        var result = await mediator.Send(cmd with { CalendarId = id, HolidayId = holidayId });
        return result.Match<IActionResult>(success: Ok, failure: ConflictAwareFailure);
    }

    [HttpDelete("{id:guid}/holidays/{holidayId:guid}")]
    [RequirePermission(ResourceType.Sla, ActionType.Edit)]
    public async Task<IActionResult> DeleteHoliday(Guid id, Guid holidayId)
    {
        var result = await mediator.Send(new DeleteSlaCalendarHolidayCommand(id, holidayId));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: NotFoundOrBadRequest);
    }

    private IActionResult NotFoundOrBadRequest(IReadOnlyList<Error> errors)
        => errors.Count > 0 && errors[0].Code == "NotFound"
            ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
            : BadRequestWithFields(errors);

    private IActionResult BadRequestWithFields(IReadOnlyList<Error> errors)
        => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) });

    private IActionResult ConflictAwareFailure(IReadOnlyList<Error> errors)
        => errors.Count > 0 && errors[0].Code == "NotFound"
            ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
            : errors.Count > 0 && errors[0].Code == "Conflict"
                ? Conflict(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) })
                : BadRequestWithFields(errors);
}
