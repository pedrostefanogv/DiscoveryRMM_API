using Discovery.Core.Cqrs.Dashboard.Dtos;

namespace Discovery.Core.Cqrs.Dashboard.Queries;

/// <summary>
/// Query to get the dashboard summary for a specific client.
/// </summary>
/// <param name="ClientId">The client identifier.</param>
/// <param name="Window">Time window for the summary data.</param>
public sealed record GetClientSummaryQuery(Guid ClientId, TimeSpan Window) : IQuery<Result<DashboardSummaryDto>>;