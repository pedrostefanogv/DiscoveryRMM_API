using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IReportDatasetQueryService
{
    Task<ReportQueryResult> QueryAsync(ReportTemplate template, string? filtersJson, CancellationToken cancellationToken = default);
}
