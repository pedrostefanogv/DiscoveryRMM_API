using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.SlaCalendars.Commands;
using Discovery.Core.Cqrs.SlaCalendars.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.SlaCalendars;

public sealed class ListSlaCalendarsQueryHandler(ISlaCalendarService svc) : IRequestHandler<ListSlaCalendarsQuery, Result<IReadOnlyList<SlaCalendarDto>>>
{
    public async Task<Result<IReadOnlyList<SlaCalendarDto>>> Handle(ListSlaCalendarsQuery q, CancellationToken ct)
    {
        var cals = await svc.GetAllAsync(q.ClientId, ct);
        return Result<IReadOnlyList<SlaCalendarDto>>.Success(cals.Select(Map).ToList().AsReadOnly());
    }

    private static SlaCalendarDto Map(SlaCalendar c) => new(c.Id, c.Name, c.ClientId, c.Timezone, c.WorkDayStartHour, c.WorkDayEndHour, c.WorkDaysJson, c.CreatedAt, c.UpdatedAt, c.Holidays.Count);
}

public sealed class GetSlaCalendarByIdQueryHandler(ISlaCalendarService svc) : IRequestHandler<GetSlaCalendarByIdQuery, Result<SlaCalendarDetailDto>>
{
    public async Task<Result<SlaCalendarDetailDto>> Handle(GetSlaCalendarByIdQuery q, CancellationToken ct)
    {
        var c = await svc.GetByIdAsync(q.Id, ct);
        return c is null
            ? Result<SlaCalendarDetailDto>.Failure(Error.NotFound($"SlaCalendar {q.Id} not found"))
            : Result<SlaCalendarDetailDto>.Success(MapDetail(c));
    }

    internal static SlaCalendarDetailDto MapDetail(SlaCalendar c) => new(
        c.Id, c.Name, c.ClientId, c.Timezone, c.WorkDayStartHour, c.WorkDayEndHour, c.WorkDaysJson,
        c.CreatedAt, c.UpdatedAt,
        c.Holidays.OrderBy(h => h.Date).Select(MapHoliday).ToList().AsReadOnly());

    internal static SlaCalendarHolidayDto MapHoliday(SlaCalendarHoliday h) => new(
        h.Id, h.Date, h.Name, h.HolidayTypeValue, h.RelativeMonth, h.RelativeDayOfWeek, h.RelativeOccurrence, h.RelativeMethodValue);
}

public sealed class CreateSlaCalendarCommandHandler(ISlaCalendarService svc) : IRequestHandler<CreateSlaCalendarCommand, Result<SlaCalendarDto>>
{
    public async Task<Result<SlaCalendarDto>> Handle(CreateSlaCalendarCommand cmd, CancellationToken ct)
    {
        var cal = new SlaCalendar { Name = cmd.Name, ClientId = cmd.ClientId, Timezone = cmd.Timezone, WorkDayStartHour = cmd.WorkDayStartHour, WorkDayEndHour = cmd.WorkDayEndHour, WorkDaysJson = cmd.WorkDaysJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var created = await svc.CreateAsync(cal, ct);
        return Result<SlaCalendarDto>.Success(new SlaCalendarDto(created.Id, created.Name, created.ClientId, created.Timezone, created.WorkDayStartHour, created.WorkDayEndHour, created.WorkDaysJson, created.CreatedAt, created.UpdatedAt, created.Holidays.Count));
    }
}

public sealed class UpdateSlaCalendarCommandHandler(ISlaCalendarService svc) : IRequestHandler<UpdateSlaCalendarCommand, Result<SlaCalendarDto>>
{
    public async Task<Result<SlaCalendarDto>> Handle(UpdateSlaCalendarCommand cmd, CancellationToken ct)
    {
        var c = await svc.GetByIdAsync(cmd.Id, ct);
        if (c is null) return Result<SlaCalendarDto>.Failure(Error.NotFound($"SlaCalendar {cmd.Id} not found"));
        if (cmd.Name is not null) c.Name = cmd.Name;
        if (cmd.Timezone is not null) c.Timezone = cmd.Timezone;
        if (cmd.WorkDayStartHour.HasValue) c.WorkDayStartHour = cmd.WorkDayStartHour.Value;
        if (cmd.WorkDayEndHour.HasValue) c.WorkDayEndHour = cmd.WorkDayEndHour.Value;
        if (cmd.WorkDaysJson is not null) c.WorkDaysJson = cmd.WorkDaysJson;
        c.UpdatedAt = DateTime.UtcNow;
        await svc.UpdateAsync(c, ct);
        return Result<SlaCalendarDto>.Success(new SlaCalendarDto(c.Id, c.Name, c.ClientId, c.Timezone, c.WorkDayStartHour, c.WorkDayEndHour, c.WorkDaysJson, c.CreatedAt, c.UpdatedAt, c.Holidays.Count));
    }
}

public sealed class DeleteSlaCalendarCommandHandler(ISlaCalendarService svc, IWorkflowProfileRepository workflowProfileRepo) : IRequestHandler<DeleteSlaCalendarCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteSlaCalendarCommand cmd, CancellationToken ct)
    {
        // Impede deixar perfis órfãos: um calendário excluído em uso faria o SLA
        // cair silenciosamente para 24x7.
        var inUse = await workflowProfileRepo.CountBySlaCalendarIdAsync(cmd.Id);
        if (inUse > 0)
            return Result<VoidResult>.Failure(Error.Conflict(
                $"O calendário está vinculado a {inUse} perfil(is) de workflow. Desvincule antes de excluí-lo."));

        await svc.DeleteAsync(cmd.Id, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

/// <summary>
/// Regras de validação de feriados, compartilhadas entre os comandos de
/// criação e atualização para manter o contrato consistente.
/// </summary>
internal static class SlaCalendarHolidayRules
{
    internal const int MinValidYear = 1900;

    internal static List<Error> Validate(
        string? name, DateTime date, int holidayType,
        int? relativeMonth, int? relativeDayOfWeek, int? relativeOccurrence, int? relativeMethod)
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(name))
            errors.Add(Error.Validation("name", "Informe o nome do feriado."));
        else if (name.Trim().Length > 255)
            errors.Add(Error.Validation("name", "O nome do feriado deve ter no máximo 255 caracteres."));

        if (holidayType is not ((int)HolidayType.Fixed or (int)HolidayType.Yearly or (int)HolidayType.Relative))
            errors.Add(Error.Validation("holidayType", "Tipo de feriado inválido (0=Fixo, 1=Anual, 2=Relativo)."));

        if (holidayType is (int)HolidayType.Fixed or (int)HolidayType.Yearly && date.Year < MinValidYear)
            errors.Add(Error.Validation("date", "Informe a data do feriado."));

        if (holidayType == (int)HolidayType.Relative)
        {
            if (relativeMonth is null or < 1 or > 12)
                errors.Add(Error.Validation("relativeMonth", "Informe o mês (1-12) do feriado relativo."));

            if (relativeOccurrence is null or < 1 or > 5)
                errors.Add(Error.Validation("relativeOccurrence", "Informe a ocorrência (1-5, sendo 5 a última)."));

            var method = relativeMethod ?? (int)RelativeHolidayMethod.DayOfWeekOccurrence;
            if (method is not ((int)RelativeHolidayMethod.DayOfWeekOccurrence or (int)RelativeHolidayMethod.NthBusinessDay))
                errors.Add(Error.Validation("relativeMethod", "Método de cálculo inválido (0=dia da semana, 1=dia útil)."));
            else if (method == (int)RelativeHolidayMethod.DayOfWeekOccurrence && relativeDayOfWeek is null or < 0 or > 6)
                errors.Add(Error.Validation("relativeDayOfWeek", "Informe o dia da semana (0=Domingo..6=Sábado)."));
        }

        return errors;
    }

    internal static bool IsDuplicate(
        SlaCalendar calendar, Guid? ignoreHolidayId, string name, DateTime date, int holidayType,
        int? relativeMonth, int? relativeDayOfWeek, int? relativeOccurrence, int? relativeMethod)
    {
        var normalizedName = name.Trim();

        foreach (var existing in calendar.Holidays)
        {
            if (ignoreHolidayId.HasValue && existing.Id == ignoreHolidayId.Value) continue;
            if (existing.HolidayTypeValue != holidayType) continue;
            if (!string.Equals(existing.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)) continue;

            var sameRule = holidayType switch
            {
                (int)HolidayType.Yearly => existing.Date.Month == date.Month && existing.Date.Day == date.Day,
                (int)HolidayType.Relative => existing.RelativeMonth == relativeMonth
                    && existing.RelativeDayOfWeek == relativeDayOfWeek
                    && existing.RelativeOccurrence == relativeOccurrence
                    && existing.RelativeMethodValue == relativeMethod,
                _ => existing.Date.Date == date.Date
            };

            if (sameRule) return true;
        }

        return false;
    }

    internal static DateTime NormalizeDate(DateTime date) => DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);

    internal static Error NotFound(Guid calendarId, Guid holidayId)
        => Error.NotFound($"SlaCalendarHoliday {holidayId} not found in SlaCalendar {calendarId}");
}

public sealed class AddSlaCalendarHolidayCommandHandler(ISlaCalendarService svc) : IRequestHandler<AddSlaCalendarHolidayCommand, Result<SlaCalendarHolidayDto>>
{
    public async Task<Result<SlaCalendarHolidayDto>> Handle(AddSlaCalendarHolidayCommand cmd, CancellationToken ct)
    {
        var calendar = await svc.GetByIdAsync(cmd.CalendarId, ct);
        if (calendar is null)
            return Result<SlaCalendarHolidayDto>.Failure(Error.NotFound($"SlaCalendar {cmd.CalendarId} not found"));

        var errors = SlaCalendarHolidayRules.Validate(cmd.Name, cmd.Date, cmd.HolidayType, cmd.RelativeMonth, cmd.RelativeDayOfWeek, cmd.RelativeOccurrence, cmd.RelativeMethod);
        if (errors.Count > 0)
            return Result<SlaCalendarHolidayDto>.Failure(errors);

        if (SlaCalendarHolidayRules.IsDuplicate(calendar, null, cmd.Name, cmd.Date, cmd.HolidayType, cmd.RelativeMonth, cmd.RelativeDayOfWeek, cmd.RelativeOccurrence, cmd.RelativeMethod))
            return Result<SlaCalendarHolidayDto>.Failure(Error.Conflict($"Já existe um feriado '{cmd.Name.Trim()}' com a mesma regra neste calendário."));

        var holiday = new SlaCalendarHoliday
        {
            CalendarId = cmd.CalendarId,
            Name = cmd.Name.Trim(),
            Date = SlaCalendarHolidayRules.NormalizeDate(cmd.Date),
            HolidayTypeValue = cmd.HolidayType,
            RelativeMonth = cmd.RelativeMonth,
            RelativeDayOfWeek = cmd.RelativeDayOfWeek,
            RelativeOccurrence = cmd.RelativeOccurrence,
            RelativeMethodValue = cmd.RelativeMethod
        };

        var created = await svc.AddHolidayAsync(holiday, ct);
        return Result<SlaCalendarHolidayDto>.Success(GetSlaCalendarByIdQueryHandler.MapHoliday(created));
    }
}

public sealed class UpdateSlaCalendarHolidayCommandHandler(ISlaCalendarService svc) : IRequestHandler<UpdateSlaCalendarHolidayCommand, Result<SlaCalendarHolidayDto>>
{
    public async Task<Result<SlaCalendarHolidayDto>> Handle(UpdateSlaCalendarHolidayCommand cmd, CancellationToken ct)
    {
        var calendar = await svc.GetByIdAsync(cmd.CalendarId, ct);
        if (calendar is null)
            return Result<SlaCalendarHolidayDto>.Failure(Error.NotFound($"SlaCalendar {cmd.CalendarId} not found"));

        var holiday = calendar.Holidays.FirstOrDefault(h => h.Id == cmd.HolidayId);
        if (holiday is null)
            return Result<SlaCalendarHolidayDto>.Failure(SlaCalendarHolidayRules.NotFound(cmd.CalendarId, cmd.HolidayId));

        var name = cmd.Name ?? holiday.Name;
        var date = cmd.Date.HasValue ? SlaCalendarHolidayRules.NormalizeDate(cmd.Date.Value) : holiday.Date;
        var holidayType = cmd.HolidayType ?? holiday.HolidayTypeValue;
        var relativeMonth = cmd.RelativeMonth ?? holiday.RelativeMonth;
        var relativeDayOfWeek = cmd.RelativeDayOfWeek ?? holiday.RelativeDayOfWeek;
        var relativeOccurrence = cmd.RelativeOccurrence ?? holiday.RelativeOccurrence;
        var relativeMethod = cmd.RelativeMethod ?? holiday.RelativeMethodValue;

        var errors = SlaCalendarHolidayRules.Validate(name, date, holidayType, relativeMonth, relativeDayOfWeek, relativeOccurrence, relativeMethod);
        if (errors.Count > 0)
            return Result<SlaCalendarHolidayDto>.Failure(errors);

        if (SlaCalendarHolidayRules.IsDuplicate(calendar, holiday.Id, name, date, holidayType, relativeMonth, relativeDayOfWeek, relativeOccurrence, relativeMethod))
            return Result<SlaCalendarHolidayDto>.Failure(Error.Conflict($"Já existe um feriado '{name.Trim()}' com a mesma regra neste calendário."));

        holiday.Name = name.Trim();
        holiday.Date = date;
        holiday.HolidayTypeValue = holidayType;
        holiday.RelativeMonth = relativeMonth;
        holiday.RelativeDayOfWeek = relativeDayOfWeek;
        holiday.RelativeOccurrence = relativeOccurrence;
        holiday.RelativeMethodValue = relativeMethod;

        await svc.UpdateHolidayAsync(holiday, ct);
        return Result<SlaCalendarHolidayDto>.Success(GetSlaCalendarByIdQueryHandler.MapHoliday(holiday));
    }
}

public sealed class DeleteSlaCalendarHolidayCommandHandler(ISlaCalendarService svc) : IRequestHandler<DeleteSlaCalendarHolidayCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteSlaCalendarHolidayCommand cmd, CancellationToken ct)
    {
        var calendar = await svc.GetByIdAsync(cmd.CalendarId, ct);
        if (calendar is null)
            return Result<VoidResult>.Failure(Error.NotFound($"SlaCalendar {cmd.CalendarId} not found"));

        var holiday = calendar.Holidays.FirstOrDefault(h => h.Id == cmd.HolidayId);
        if (holiday is null)
            return Result<VoidResult>.Failure(SlaCalendarHolidayRules.NotFound(cmd.CalendarId, cmd.HolidayId));

        await svc.DeleteHolidayAsync(holiday.Id, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
