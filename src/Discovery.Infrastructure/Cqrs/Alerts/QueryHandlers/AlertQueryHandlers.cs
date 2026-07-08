using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Alerts.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Alerts.QueryHandlers;

public sealed class ListAgentAlertsQueryHandler(
    IAgentAlertRepository repo
) : IRequestHandler<ListAgentAlertsQuery, Result<ListAlertsResult>>
{
    public async Task<Result<ListAlertsResult>> Handle(ListAgentAlertsQuery q, CancellationToken ct)
    {
        var status = q.Status is not null && Enum.TryParse<AlertDefinitionStatus>(q.Status, true, out var s) ? s : null as AlertDefinitionStatus?;
        var scopeType = q.ScopeType is not null && Enum.TryParse<AlertScopeType>(q.ScopeType, true, out var st) ? st : null as AlertScopeType?;

        var items = await repo.GetByFiltersPageAsync(
            status, scopeType, q.ScopeClientId, q.ScopeSiteId, q.ScopeAgentId, q.TicketId, q.Cursor, q.Limit);

        var dtos = items.Select(a => new AlertDto(a.Id, a.ScopeAgentId, a.Title, a.AlertType.ToString(), a.Status.ToString(), a.CreatedAt)).ToList() as IReadOnlyList<AlertDto>;
        return Result<ListAlertsResult>.Success(new ListAlertsResult(dtos, null, false, dtos.Count));
    }
}

public sealed class GetAlertByIdQueryHandler(
    IAgentAlertRepository repo
) : IRequestHandler<GetAlertByIdQuery, Result<AlertDetailDto>>
{
    public async Task<Result<AlertDetailDto>> Handle(GetAlertByIdQuery q, CancellationToken ct)
    {
        var alert = await repo.GetByIdAsync(q.Id);
        if (alert is null)
            return Result<AlertDetailDto>.Failure(Error.NotFound($"Alert {q.Id} not found"));

        return Result<AlertDetailDto>.Success(new AlertDetailDto(
            alert.Id, alert.ScopeAgentId, alert.ScopeSiteId, alert.ScopeClientId,
            alert.Title, alert.Message, alert.AlertType.ToString(), alert.Status.ToString(),
            alert.CreatedAt, null, alert.TicketId));
    }
}