using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Dashboard.Dtos;
using Discovery.Core.Cqrs.Dashboard.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Dashboard.QueryHandlers;

using CqrsDto = Discovery.Core.Cqrs.Dashboard.Dtos.DashboardSummaryDto;
using LegacyDto = Discovery.Core.DTOs.DashboardSummaryDto;

public sealed class GetGlobalSummaryQueryHandler(IDashboardService svc)
    : IRequestHandler<GetGlobalSummaryQuery, Result<CqrsDto>>
{
    public async Task<Result<CqrsDto>> Handle(GetGlobalSummaryQuery q, CancellationToken ct)
    {
        var d = await svc.GetGlobalSummaryAsync(q.Window, ct);
        return Result<CqrsDto>.Success(Map(d));
    }
    internal static CqrsDto Map(LegacyDto d) => new(
        new DashboardScopeDto(d.Scope.Level, d.Scope.ClientId, d.Scope.SiteId),
        new DashboardPeriodDto(d.Period.FromUtc, d.Period.ToUtc, d.Period.WindowHours),
        new DashboardClientsSummaryDto(d.Clients.Total, d.Clients.Active),
        new DashboardSitesSummaryDto(d.Sites.Total),
        new DashboardAgentsSummaryDto(d.Agents.Total, d.Agents.Online, d.Agents.Offline, d.Agents.Stale, d.Agents.Maintenance, d.Agents.Error, d.Agents.OnlineGraceSeconds),
        new DashboardCommandsSummaryDto(d.Commands.Total, d.Commands.Pending, d.Commands.Sent, d.Commands.Running, d.Commands.Completed, d.Commands.Failed, d.Commands.SuccessRate),
        new DashboardTicketsSummaryDto(d.Tickets.Total, d.Tickets.Open, d.Tickets.Closed, d.Tickets.SlaBreachedOpen),
        new DashboardLogsSummaryDto(d.Logs.Total, d.Logs.Error, d.Logs.Warn, d.Logs.Info),
        new DashboardAutomationSummaryDto(d.Automation.Total, d.Automation.Dispatched, d.Automation.Acknowledged, d.Automation.Completed, d.Automation.Failed, d.Automation.SuccessRate),
        d.GeneratedAtUtc);
}

public sealed class GetClientSummaryQueryHandler(IDashboardService svc)
    : IRequestHandler<GetClientSummaryQuery, Result<CqrsDto>>
{
    public async Task<Result<CqrsDto>> Handle(GetClientSummaryQuery q, CancellationToken ct)
    {
        var d = await svc.GetClientSummaryAsync(q.ClientId, q.Window, ct);
        return Result<CqrsDto>.Success(GetGlobalSummaryQueryHandler.Map(d));
    }
}

public sealed class GetSiteSummaryQueryHandler(IDashboardService svc)
    : IRequestHandler<GetSiteSummaryQuery, Result<CqrsDto>>
{
    public async Task<Result<CqrsDto>> Handle(GetSiteSummaryQuery q, CancellationToken ct)
    {
        var d = await svc.GetSiteSummaryAsync(q.ClientId, q.SiteId, q.Window, ct);
        return Result<CqrsDto>.Success(GetGlobalSummaryQueryHandler.Map(d));
    }
}
