using Meduza.Core.Entities;
using Meduza.Core.ValueObjects;

namespace Meduza.Core.Interfaces;

public interface IReportDatasetQueryService
{
    Task<ReportQueryResult> QueryAsync(ReportTemplate template, string? filtersJson, CancellationToken cancellationToken = default);
}
