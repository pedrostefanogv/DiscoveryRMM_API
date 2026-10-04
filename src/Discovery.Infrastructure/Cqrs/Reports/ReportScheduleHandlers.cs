using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Reports;

public sealed class ListReportSchedulesQueryHandler(IReportScheduleRepository repo)
    : IRequestHandler<ListReportSchedulesQuery, Result<IReadOnlyList<ReportScheduleDto>>>
{
    public async Task<Result<IReadOnlyList<ReportScheduleDto>>> Handle(ListReportSchedulesQuery q, CancellationToken ct)
    {
        var schedules = await repo.GetAllAsync(q.ClientId, q.IsActive);
        return Result<IReadOnlyList<ReportScheduleDto>>.Success(schedules.Select(Map).ToList());
    }

    internal static ReportScheduleDto Map(ReportSchedule schedule)
    {
        var (frequency, dayOfWeek, dayOfMonth, hourUtc, minuteUtc) = ReportScheduleCalculator.ParseCron(schedule.CronExpression);

        return new ReportScheduleDto(
            schedule.Id,
            schedule.TemplateId,
            schedule.ClientId,
            string.IsNullOrWhiteSpace(schedule.ScheduleLabel) ? "Agendamento" : schedule.ScheduleLabel,
            frequency,
            dayOfWeek,
            dayOfMonth,
            hourUtc,
            minuteUtc,
            (int)schedule.Format,
            schedule.FiltersJson,
            SplitRecipients(schedule.Recipients),
            schedule.IsActive,
            schedule.LastTriggeredAt,
            schedule.NextTriggerAt,
            schedule.CreatedAt,
            schedule.UpdatedAt);
    }

    internal static string? JoinRecipients(IReadOnlyList<string>? recipients)
        => recipients is { Count: > 0 }
            ? string.Join(',', recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()))
            : null;

    private static IReadOnlyList<string>? SplitRecipients(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class GetReportScheduleQueryHandler(IReportScheduleRepository repo)
    : IRequestHandler<GetReportScheduleQuery, Result<ReportScheduleDto>>
{
    public async Task<Result<ReportScheduleDto>> Handle(GetReportScheduleQuery q, CancellationToken ct)
    {
        var schedule = await repo.GetByIdAsync(q.Id, q.ClientId);
        if (schedule is null)
            return Result<ReportScheduleDto>.Failure(Error.NotFound($"ReportSchedule {q.Id} not found"));

        return Result<ReportScheduleDto>.Success(ListReportSchedulesQueryHandler.Map(schedule));
    }
}

public sealed class CreateReportScheduleCommandHandler(
    IReportScheduleRepository schedules,
    IReportTemplateRepository templates)
    : IRequestHandler<CreateReportScheduleCommand, Result<ReportScheduleDto>>
{
    public async Task<Result<ReportScheduleDto>> Handle(CreateReportScheduleCommand cmd, CancellationToken ct)
    {
        if (cmd.Frequency is < 0 or > 2)
            return Result<ReportScheduleDto>.Failure(Error.Validation("frequency", "Frequencia invalida (0=diario, 1=semanal, 2=mensal)."));

        var template = await templates.GetByIdAsync(cmd.TemplateId, cmd.ClientId);
        if (template is null)
            return Result<ReportScheduleDto>.Failure(Error.NotFound($"ReportTemplate {cmd.TemplateId} not found"));

        // Formato: o pedido; se ausente/invalido, o padrao do template; senao Markdown.
        var scheduledFormat = ReportFormatResolver.Resolve(cmd.Format)
            ?? ReportFormatResolver.Resolve(template.DefaultFormat)
            ?? ReportFormat.Markdown;

        var now = DateTime.UtcNow;
        var schedule = new ReportSchedule
        {
            TemplateId = cmd.TemplateId,
            ClientId = cmd.ClientId ?? template.ClientId,
            Format = scheduledFormat,
            FiltersJson = cmd.FiltersJson,
            ScheduleLabel = string.IsNullOrWhiteSpace(cmd.Name) ? template.Name : cmd.Name,
            CronExpression = ReportScheduleCalculator.BuildCron(cmd.Frequency, cmd.DayOfWeek, cmd.DayOfMonth, cmd.HourUtc, cmd.MinuteUtc),
            TimeZoneId = "UTC",
            IsActive = cmd.IsActive,
            // Entrega por e-mail/webhook ainda nao existe: os destinatarios ficam
            // persistidos, mas o arquivo continua apenas no storage.
            DeliveryMode = "storage",
            Recipients = ListReportSchedulesQueryHandler.JoinRecipients(cmd.Recipients),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = cmd.CreatedBy,
            UpdatedBy = cmd.CreatedBy,
            NextTriggerAt = cmd.IsActive
                ? ReportScheduleCalculator.ComputeNextOccurrenceUtc(cmd.Frequency, cmd.DayOfWeek, cmd.DayOfMonth, cmd.HourUtc, cmd.MinuteUtc, now)
                : null
        };

        var created = await schedules.CreateAsync(schedule);
        return Result<ReportScheduleDto>.Success(ListReportSchedulesQueryHandler.Map(created));
    }
}

public sealed class UpdateReportScheduleCommandHandler(IReportScheduleRepository schedules)
    : IRequestHandler<UpdateReportScheduleCommand, Result<ReportScheduleDto>>
{
    public async Task<Result<ReportScheduleDto>> Handle(UpdateReportScheduleCommand cmd, CancellationToken ct)
    {
        var schedule = await schedules.GetByIdAsync(cmd.Id, cmd.ClientId);
        if (schedule is null)
            return Result<ReportScheduleDto>.Failure(Error.NotFound($"ReportSchedule {cmd.Id} not found"));

        if (cmd.Frequency.HasValue && cmd.Frequency.Value is < 0 or > 2)
            return Result<ReportScheduleDto>.Failure(Error.Validation("frequency", "Frequencia invalida (0=diario, 1=semanal, 2=mensal)."));

        var resolvedFormat = cmd.Format is null ? (ReportFormat?)null : ReportFormatResolver.Resolve(cmd.Format);
        if (cmd.Format is not null && resolvedFormat is null)
            return Result<ReportScheduleDto>.Failure(Error.Validation(
                "format",
                $"Format {cmd.Format} is not supported. Supported formats: {ReportFormatResolver.SupportedList()}."));

        if (cmd.Name is not null) schedule.ScheduleLabel = cmd.Name;
        if (resolvedFormat.HasValue) schedule.Format = resolvedFormat.Value;
        if (cmd.FiltersJson is not null) schedule.FiltersJson = cmd.FiltersJson;
        if (cmd.Recipients is not null) schedule.Recipients = ListReportSchedulesQueryHandler.JoinRecipients(cmd.Recipients);
        if (cmd.IsActive.HasValue) schedule.IsActive = cmd.IsActive.Value;

        var (currentFrequency, currentDayOfWeek, currentDayOfMonth, currentHour, currentMinute) =
            ReportScheduleCalculator.ParseCron(schedule.CronExpression);

        var frequency = cmd.Frequency ?? currentFrequency;
        var dayOfWeek = cmd.DayOfWeek ?? currentDayOfWeek;
        var dayOfMonth = cmd.DayOfMonth ?? currentDayOfMonth;
        var hour = cmd.HourUtc ?? currentHour;
        var minute = cmd.MinuteUtc ?? currentMinute;

        var scheduleChanged = cmd.Frequency.HasValue || cmd.DayOfWeek.HasValue || cmd.DayOfMonth.HasValue
            || cmd.HourUtc.HasValue || cmd.MinuteUtc.HasValue;

        if (scheduleChanged)
        {
            schedule.CronExpression = ReportScheduleCalculator.BuildCron(frequency, dayOfWeek, dayOfMonth, hour, minute);
            schedule.NextTriggerAt = schedule.IsActive
                ? ReportScheduleCalculator.ComputeNextOccurrenceUtc(frequency, dayOfWeek, dayOfMonth, hour, minute, DateTime.UtcNow)
                : null;
        }
        else if (!schedule.IsActive)
        {
            schedule.NextTriggerAt = null;
        }

        schedule.UpdatedAt = DateTime.UtcNow;
        schedule.UpdatedBy = cmd.UpdatedBy;
        await schedules.UpdateAsync(schedule);

        return Result<ReportScheduleDto>.Success(ListReportSchedulesQueryHandler.Map(schedule));
    }
}

public sealed class DeleteReportScheduleCommandHandler(IReportScheduleRepository schedules)
    : IRequestHandler<DeleteReportScheduleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteReportScheduleCommand cmd, CancellationToken ct)
    {
        var deleted = await schedules.DeleteAsync(cmd.Id, cmd.ClientId);
        if (!deleted)
            return Result<VoidResult>.Failure(Error.NotFound($"ReportSchedule {cmd.Id} not found"));

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
