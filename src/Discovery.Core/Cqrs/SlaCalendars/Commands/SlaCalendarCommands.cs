using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.SlaCalendars.Commands;

public sealed record CreateSlaCalendarCommand(string Name, Guid? ClientId, string Timezone, int WorkDayStartHour, int WorkDayEndHour, string WorkDaysJson) : ICommand<Result<SlaCalendarDto>>;
public sealed record UpdateSlaCalendarCommand(Guid Id, string? Name, string? Timezone, int? WorkDayStartHour, int? WorkDayEndHour, string? WorkDaysJson) : ICommand<Result<SlaCalendarDto>>;
public sealed record DeleteSlaCalendarCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>
/// Calendário de SLA na forma de listagem. <see cref="HolidayCount"/> evita
/// hidratar todos os feriados só para exibir a contagem na tela.
/// </summary>
public sealed record SlaCalendarDto(Guid Id, string Name, Guid? ClientId, string Timezone, int WorkDayStartHour, int WorkDayEndHour, string WorkDaysJson, DateTime CreatedAt, DateTime UpdatedAt, int HolidayCount);

/// <summary>Detalhe do calendário, incluindo os feriados cadastrados.</summary>
public sealed record SlaCalendarDetailDto(Guid Id, string Name, Guid? ClientId, string Timezone, int WorkDayStartHour, int WorkDayEndHour, string WorkDaysJson, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<SlaCalendarHolidayDto> Holidays);

/// <summary>Feriado ou dia de exceção de um calendário de SLA.</summary>
public sealed record SlaCalendarHolidayDto(Guid Id, DateTime Date, string Name, int HolidayType, int? RelativeMonth, int? RelativeDayOfWeek, int? RelativeOccurrence, int? RelativeMethod);

public sealed record AddSlaCalendarHolidayCommand(Guid CalendarId, string Name, DateTime Date, int HolidayType, int? RelativeMonth, int? RelativeDayOfWeek, int? RelativeOccurrence, int? RelativeMethod) : ICommand<Result<SlaCalendarHolidayDto>>;
public sealed record UpdateSlaCalendarHolidayCommand(Guid CalendarId, Guid HolidayId, string? Name, DateTime? Date, int? HolidayType, int? RelativeMonth, int? RelativeDayOfWeek, int? RelativeOccurrence, int? RelativeMethod) : ICommand<Result<SlaCalendarHolidayDto>>;
public sealed record DeleteSlaCalendarHolidayCommand(Guid CalendarId, Guid HolidayId) : ICommand<Result<VoidResult>>;
