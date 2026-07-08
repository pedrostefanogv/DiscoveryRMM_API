using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Reports.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Reports;

public sealed class ListReportsQueryHandler : IRequestHandler<ListReportsQuery, Result<IReadOnlyList<ReportDto>>>
{
    public Task<Result<IReadOnlyList<ReportDto>>> Handle(ListReportsQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<ReportDto>>.Success(Array.Empty<ReportDto>()));
}

public sealed class GetReportExecutionQueryHandler : IRequestHandler<GetReportExecutionQuery, Result<ReportDto>>
{
    public Task<Result<ReportDto>> Handle(GetReportExecutionQuery q, CancellationToken ct) => Task.FromResult(Result<ReportDto>.Failure(Error.NotFound($"Report {q.ExecutionId} not found")));
}
