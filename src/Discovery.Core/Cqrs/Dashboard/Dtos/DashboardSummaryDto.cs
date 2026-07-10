using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Dashboard.Dtos;

public sealed record DashboardSummaryDto(
    DashboardScopeDto Scope,
    DashboardPeriodDto Period,
    DashboardClientsSummaryDto Clients,
    DashboardSitesSummaryDto Sites,
    DashboardAgentsSummaryDto Agents,
    DashboardCommandsSummaryDto Commands,
    DashboardTicketsSummaryDto Tickets,
    DashboardLogsSummaryDto Logs,
    DashboardAutomationSummaryDto Automation,
    DateTime GeneratedAtUtc);

public sealed record DashboardScopeDto(string Level, Guid? ClientId, Guid? SiteId);
public sealed record DashboardPeriodDto(DateTime FromUtc, DateTime ToUtc, int WindowHours);
public sealed record DashboardClientsSummaryDto(int Total, int Active);
public sealed record DashboardSitesSummaryDto(int Total);
public sealed record DashboardAgentsSummaryDto(int Total, int Online, int Offline, int Stale, int Maintenance, int Error, int OnlineGraceSeconds);
public sealed record DashboardCommandsSummaryDto(int Total, int Pending, int Sent, int Running, int Completed, int Failed, double SuccessRate);
public sealed record DashboardTicketsSummaryDto(int Total, int Open, int Closed, int SlaBreachedOpen);
public sealed record DashboardLogsSummaryDto(int Total, int Error, int Warn, int Info);
public sealed record DashboardAutomationSummaryDto(int Total, int Dispatched, int Acknowledged, int Completed, int Failed, double SuccessRate);