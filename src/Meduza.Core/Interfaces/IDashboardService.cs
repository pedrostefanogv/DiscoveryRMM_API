using Meduza.Core.DTOs;

namespace Meduza.Core.Interfaces;

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetGlobalSummaryAsync(TimeSpan window, CancellationToken cancellationToken = default);
    Task<DashboardSummaryDto> GetClientSummaryAsync(Guid clientId, TimeSpan window, CancellationToken cancellationToken = default);
    Task<DashboardSummaryDto> GetSiteSummaryAsync(Guid clientId, Guid siteId, TimeSpan window, CancellationToken cancellationToken = default);
}
