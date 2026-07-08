using Discovery.Core.Cqrs.Dashboard.Dtos;

namespace Discovery.Core.Cqrs.Dashboard.Queries;

/// <summary>
/// Query to get the global dashboard summary.
/// </summary>
/// <param name="Window">Time window for the summary data (e.g., 24 hours).</param>
public sealed record GetGlobalSummaryQuery(TimeSpan Window) : IQuery<Result<DashboardSummaryDto>>;