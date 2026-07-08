using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

public sealed class CreateTicketAlertRuleCommandHandler(ITicketAlertRuleRepository repo) : IRequestHandler<CreateTicketAlertRuleCommand, Result<TicketAlertRule>>
{
    public async Task<Result<TicketAlertRule>> Handle(CreateTicketAlertRuleCommand cmd, CancellationToken ct)
    {
        var e = new TicketAlertRule { WorkflowStateId = cmd.WorkflowStateId, Title = cmd.Title, Message = cmd.Message, AlertType = cmd.AlertType, TimeoutSeconds = cmd.TimeoutSeconds, ActionsJson = cmd.ActionsJson, DefaultAction = cmd.DefaultAction, Icon = cmd.Icon, ScopePreference = cmd.ScopePreference, IsEnabled = cmd.IsEnabled };
        return Result<TicketAlertRule>.Success(await repo.CreateAsync(e));
    }
}

public sealed class UpdateTicketAlertRuleCommandHandler(ITicketAlertRuleRepository repo) : IRequestHandler<UpdateTicketAlertRuleCommand, Result<TicketAlertRule>>
{
    public async Task<Result<TicketAlertRule>> Handle(UpdateTicketAlertRuleCommand cmd, CancellationToken ct)
    {
        var e = await repo.GetByIdAsync(cmd.Id);
        if (e is null) return Result<TicketAlertRule>.Failure(Error.NotFound("Alert rule not found."));
        e.WorkflowStateId = cmd.WorkflowStateId; e.Title = cmd.Title; e.Message = cmd.Message; e.AlertType = cmd.AlertType; e.TimeoutSeconds = cmd.TimeoutSeconds; e.ActionsJson = cmd.ActionsJson; e.DefaultAction = cmd.DefaultAction; e.Icon = cmd.Icon; e.ScopePreference = cmd.ScopePreference; e.IsEnabled = cmd.IsEnabled;
        return Result<TicketAlertRule>.Success(await repo.UpdateAsync(e));
    }
}

public sealed class ToggleTicketAlertRuleCommandHandler(ITicketAlertRuleRepository repo) : IRequestHandler<ToggleTicketAlertRuleCommand, Result<TicketAlertRule>>
{
    public async Task<Result<TicketAlertRule>> Handle(ToggleTicketAlertRuleCommand cmd, CancellationToken ct)
    {
        var e = await repo.GetByIdAsync(cmd.Id);
        if (e is null) return Result<TicketAlertRule>.Failure(Error.NotFound("Alert rule not found."));
        e.IsEnabled = !e.IsEnabled;
        return Result<TicketAlertRule>.Success(await repo.UpdateAsync(e));
    }
}

public sealed class DeleteTicketAlertRuleCommandHandler(ITicketAlertRuleRepository repo) : IRequestHandler<DeleteTicketAlertRuleCommand, Result<VoidResult>>
{ public async Task<Result<VoidResult>> Handle(DeleteTicketAlertRuleCommand cmd, CancellationToken ct) { await repo.DeleteAsync(cmd.Id); return Result<VoidResult>.Success(VoidResult.Value); } }
