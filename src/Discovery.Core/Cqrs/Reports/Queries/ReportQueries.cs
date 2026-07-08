using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Reports.Queries;

public sealed record ListReportsQuery(Guid? ClientId) : IQuery<Result<IReadOnlyList<ReportDto>>>;
public sealed record GetReportExecutionQuery(Guid ExecutionId, Guid? ClientId) : IQuery<Result<ReportDto>>;

public sealed record ReportDto(Guid Id, string TemplateName, string Status, string Format, DateTime CreatedAt, DateTime? CompletedAt);
