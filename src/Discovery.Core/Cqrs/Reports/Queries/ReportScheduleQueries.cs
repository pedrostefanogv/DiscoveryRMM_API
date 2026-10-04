using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Reports.Queries;

public sealed record ReportScheduleDto(
    Guid Id,
    Guid TemplateId,
    Guid? ClientId,
    string Name,
    int Frequency,
    int? DayOfWeek,
    int? DayOfMonth,
    int HourUtc,
    int MinuteUtc,
    int Format,
    string? FiltersJson,
    IReadOnlyList<string>? Recipients,
    bool IsActive,
    DateTime? LastRunAt,
    DateTime? NextRunAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ListReportSchedulesQuery(
    Guid? ClientId = null,
    bool? IsActive = null) : IQuery<Result<IReadOnlyList<ReportScheduleDto>>>;

public sealed record GetReportScheduleQuery(Guid Id, Guid? ClientId = null)
    : IQuery<Result<ReportScheduleDto>>;

public sealed record CreateReportScheduleCommand(
    Guid TemplateId,
    string? Name,
    int Frequency,
    int? DayOfWeek,
    int? DayOfMonth,
    int HourUtc,
    int MinuteUtc,
    int Format,
    string? FiltersJson = null,
    IReadOnlyList<string>? Recipients = null,
    bool IsActive = true,
    Guid? ClientId = null,
    string? CreatedBy = null) : ICommand<Result<ReportScheduleDto>>;

public sealed record UpdateReportScheduleCommand(
    Guid Id,
    string? Name = null,
    int? Frequency = null,
    int? DayOfWeek = null,
    int? DayOfMonth = null,
    int? HourUtc = null,
    int? MinuteUtc = null,
    int? Format = null,
    string? FiltersJson = null,
    IReadOnlyList<string>? Recipients = null,
    bool? IsActive = null,
    Guid? ClientId = null,
    string? UpdatedBy = null) : ICommand<Result<ReportScheduleDto>>;

public sealed record DeleteReportScheduleCommand(Guid Id, Guid? ClientId = null)
    : ICommand<Result<VoidResult>>;
