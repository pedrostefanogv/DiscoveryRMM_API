using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Alerts.Commands;
using Discovery.Core.Cqrs.Alerts.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Alerts.CommandHandlers;

public sealed class CreateAlertCommandHandler(
    IAgentAlertService alertService
) : IRequestHandler<CreateAlertCommand, Result<AlertDetailDto>>
{
    public async Task<Result<AlertDetailDto>> Handle(CreateAlertCommand cmd, CancellationToken ct)
    {
        var request = new CreateAgentAlertRequest(
            cmd.Title, cmd.Description,
            cmd.Severity is not null && Enum.TryParse<PsadtAlertType>(cmd.Severity, true, out var t) ? t : PsadtAlertType.Toast,
            null, null, null, "info",
            cmd.AgentId.HasValue ? AlertScopeType.Agent
                : cmd.SiteId.HasValue ? AlertScopeType.Site
                : cmd.ClientId.HasValue ? AlertScopeType.Client
                : AlertScopeType.Agent,
            cmd.AgentId, cmd.SiteId, cmd.ClientId, null,
            null, null, null, null);
        var created = await alertService.CreateAsync(request, ct);
        return Result<AlertDetailDto>.Success(new AlertDetailDto(
            created.Id, created.ScopeAgentId, created.ScopeSiteId, created.ScopeClientId,
            created.Title, created.Message, created.AlertType.ToString(), created.Status.ToString(),
            created.CreatedAt, null, null));
    }
}

public sealed class DispatchAlertCommandHandler(
    IAgentAlertRepository alertRepo,
    IAlertDispatchService dispatchService
) : IRequestHandler<DispatchAlertCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DispatchAlertCommand cmd, CancellationToken ct)
    {
        var alert = await alertRepo.GetByIdAsync(cmd.AlertId);
        if (alert is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Alert {cmd.AlertId} not found"));

        await dispatchService.DispatchAsync(alert, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class CancelAlertCommandHandler(
    IAgentAlertService alertService
) : IRequestHandler<CancelAlertCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(CancelAlertCommand cmd, CancellationToken ct)
    {
        var cancelled = await alertService.CancelAsync(cmd.AlertId);
        if (!cancelled)
            return Result<VoidResult>.Failure(Error.Validation("AlertId", "Alert cannot be cancelled in its current state."));
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class CreateTicketFromAlertCommandHandler(
    IAgentAlertRepository alertRepo,
    IAlertToTicketService alertToTicketService
) : IRequestHandler<CreateTicketFromAlertCommand, Result<AlertDetailDto>>
{
    public async Task<Result<AlertDetailDto>> Handle(CreateTicketFromAlertCommand cmd, CancellationToken ct)
    {
        var alert = await alertRepo.GetByIdAsync(cmd.AlertId);
        if (alert is null)
            return Result<AlertDetailDto>.Failure(Error.NotFound($"Alert {cmd.AlertId} not found"));

        // Obtém client/site/agent do escopo do alerta
        var clientId = alert.ScopeClientId ?? Guid.Empty;
        if (clientId == Guid.Empty)
            return Result<AlertDetailDto>.Failure(Error.Validation("AlertId", "Alert has no client scope assigned"));

        var ticket = await alertToTicketService.CreateTicketFromAlertAsync(
            alert, clientId, alert.ScopeSiteId, alert.ScopeAgentId,
            ct: ct);

        // Atualiza o alerta com o ticket criado
        alert.TicketId = ticket.Id;

        return Result<AlertDetailDto>.Success(new AlertDetailDto(
            alert.Id, alert.ScopeAgentId, alert.ScopeSiteId, alert.ScopeClientId,
            alert.Title, alert.Message, alert.AlertType.ToString(), alert.Status.ToString(),
            alert.CreatedAt, null, alert.TicketId));
    }
}