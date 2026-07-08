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
    private static SlaCalendarDto Map(SlaCalendar c) => new(c.Id, c.Name, c.ClientId, c.Timezone, c.WorkDayStartHour, c.WorkDayEndHour, c.WorkDaysJson, c.CreatedAt, c.UpdatedAt);
}

public sealed class GetSlaCalendarByIdQueryHandler(ISlaCalendarService svc) : IRequestHandler<GetSlaCalendarByIdQuery, Result<SlaCalendarDto>>
{
    public async Task<Result<SlaCalendarDto>> Handle(GetSlaCalendarByIdQuery q, CancellationToken ct)
    {
        var c = await svc.GetByIdAsync(q.Id, ct);
        return c is null ? Result<SlaCalendarDto>.Failure(Error.NotFound($"SlaCalendar {q.Id} not found")) : Result<SlaCalendarDto>.Success(new SlaCalendarDto(c.Id, c.Name, c.ClientId, c.Timezone, c.WorkDayStartHour, c.WorkDayEndHour, c.WorkDaysJson, c.CreatedAt, c.UpdatedAt));
    }
}

public sealed class CreateSlaCalendarCommandHandler(ISlaCalendarService svc) : IRequestHandler<CreateSlaCalendarCommand, Result<SlaCalendarDto>>
{
    public async Task<Result<SlaCalendarDto>> Handle(CreateSlaCalendarCommand cmd, CancellationToken ct)
    {
        var cal = new SlaCalendar { Name = cmd.Name, ClientId = cmd.ClientId, Timezone = cmd.Timezone, WorkDayStartHour = cmd.WorkDayStartHour, WorkDayEndHour = cmd.WorkDayEndHour, WorkDaysJson = cmd.WorkDaysJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var created = await svc.CreateAsync(cal, ct);
        return Result<SlaCalendarDto>.Success(new SlaCalendarDto(created.Id, created.Name, created.ClientId, created.Timezone, created.WorkDayStartHour, created.WorkDayEndHour, created.WorkDaysJson, created.CreatedAt, created.UpdatedAt));
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
        return Result<SlaCalendarDto>.Success(new SlaCalendarDto(c.Id, c.Name, c.ClientId, c.Timezone, c.WorkDayStartHour, c.WorkDayEndHour, c.WorkDaysJson, c.CreatedAt, c.UpdatedAt));
    }
}

public sealed class DeleteSlaCalendarCommandHandler(ISlaCalendarService svc) : IRequestHandler<DeleteSlaCalendarCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteSlaCalendarCommand cmd, CancellationToken ct)
    {
        await svc.DeleteAsync(cmd.Id, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
