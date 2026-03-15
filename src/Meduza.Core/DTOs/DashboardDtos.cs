namespace Meduza.Core.DTOs;

public record DashboardSummaryDto(
    DashboardScopeDto Scope,
    DashboardPeriodDto Period,
    DashboardAgentsSummaryDto Agents,
    DashboardCommandsSummaryDto Commands,
    DashboardTicketsSummaryDto Tickets,
    DashboardLogsSummaryDto Logs,
    DashboardAutomationSummaryDto Automation,
    DateTime GeneratedAtUtc);

public record DashboardScopeDto(
    string Level,
    Guid? ClientId,
    Guid? SiteId);

public record DashboardPeriodDto(
    DateTime FromUtc,
    DateTime ToUtc,
    int WindowHours);

public record DashboardAgentsSummaryDto(
    int Total,
    int Online,
    int Offline,
    int Stale,
    int Maintenance,
    int Error,
    int OnlineGraceSeconds);

public record DashboardCommandsSummaryDto(
    int Total,
    int Pending,
    int Sent,
    int Running,
    int Completed,
    int Failed,
    double SuccessRate);

public record DashboardTicketsSummaryDto(
    int Total,
    int Open,
    int Closed,
    int SlaBreachedOpen);

public record DashboardLogsSummaryDto(
    int Total,
    int Error,
    int Warn,
    int Info);

public record DashboardAutomationSummaryDto(
    int Total,
    int Dispatched,
    int Acknowledged,
    int Completed,
    int Failed,
    double SuccessRate);
