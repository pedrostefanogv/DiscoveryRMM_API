using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Dashboard.Dtos;

/// <summary>
/// Read-optimized DTO for dashboard summary queries.
/// Same shape as the existing DashboardSummaryDto but defined in the Cqrs namespace
/// for eventual replacement of the legacy DTO.
/// </summary>
public sealed record DashboardSummaryDto(
    DashboardScopeDto Scope,
    DashboardPeriodDto Period,
    DashboardAgentsSummaryDto Agents,
    DashboardCommandsSummaryDto Commands,
    DashboardTicketsSummaryDto Tickets,
    DashboardLogsSummaryDto Logs,
    DashboardAutomationSummaryDto Automation,
    DateTime GeneratedAtUtc);

public sealed record DashboardScopeDto(string Level, Guid? ClientId, Guid? SiteId);
public sealed record DashboardPeriodDto(DateTime FromUtc, DateTime ToUtc, int WindowHours);
public sealed record DashboardAgentsSummaryDto(int Total, int Online, int Offline, int Stale, int Maintenance, int Error, int OnlineGraceSeconds);
public sealed record DashboardCommandsSummaryDto(int Total, int Pending, int Sent, int Running, int Completed, int Failed, double SuccessRate);
public sealed record DashboardTicketsSummaryDto(int Total, int Open, int Closed, int SlaBreachedOpen);
public sealed record DashboardLogsSummaryDto(int Total, int Error, int Warn, int Info);
public sealed record DashboardAutomationSummaryDto(int Total, int Dispatched, int Acknowledged, int Completed, int Failed, double SuccessRate);