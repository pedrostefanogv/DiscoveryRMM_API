using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.SlaCalendars.Commands;

public sealed record CreateSlaCalendarCommand(string Name, Guid? ClientId, string Timezone, int WorkDayStartHour, int WorkDayEndHour, string WorkDaysJson) : ICommand<Result<SlaCalendarDto>>;
public sealed record UpdateSlaCalendarCommand(Guid Id, string? Name, string? Timezone, int? WorkDayStartHour, int? WorkDayEndHour, string? WorkDaysJson) : ICommand<Result<SlaCalendarDto>>;
public sealed record DeleteSlaCalendarCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record SlaCalendarDto(Guid Id, string Name, Guid? ClientId, string Timezone, int WorkDayStartHour, int WorkDayEndHour, string WorkDaysJson, DateTime CreatedAt, DateTime UpdatedAt);