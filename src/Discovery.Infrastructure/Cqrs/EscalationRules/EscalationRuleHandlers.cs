using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.EscalationRules.Commands;
using Discovery.Core.Cqrs.EscalationRules.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.EscalationRules;

public sealed class ListEscalationRulesQueryHandler(IEscalationRuleService svc) : IRequestHandler<ListEscalationRulesQuery, Result<IReadOnlyList<EscalationRuleDto>>>
{
    public async Task<Result<IReadOnlyList<EscalationRuleDto>>> Handle(ListEscalationRulesQuery q, CancellationToken ct)
    {
        IReadOnlyList<TicketEscalationRule> rules;
        if (q.WorkflowProfileId.HasValue) rules = await svc.GetByWorkflowProfileIdAsync(q.WorkflowProfileId.Value, ct);
        else rules = Array.Empty<TicketEscalationRule>();
        return Result<IReadOnlyList<EscalationRuleDto>>.Success(rules.Select(Map).ToList().AsReadOnly());
    }
    private static EscalationRuleDto Map(TicketEscalationRule r) => new(r.Id, r.WorkflowProfileId, r.Name, r.TriggerAtSlaPercent, r.TriggerAtHoursBefore, r.ReassignToUserId, r.ReassignToDepartmentId, r.BumpPriority, r.NotifyAssignee, r.IsActive, r.CreatedAt, r.UpdatedAt);
}

public sealed class GetEscalationRuleByIdQueryHandler(IEscalationRuleService svc) : IRequestHandler<GetEscalationRuleByIdQuery, Result<EscalationRuleDto>>
{
    public async Task<Result<EscalationRuleDto>> Handle(GetEscalationRuleByIdQuery q, CancellationToken ct)
    {
        var r = await svc.GetByIdAsync(q.Id, ct);
        if (r is null) return Result<EscalationRuleDto>.Failure(Error.NotFound($"Rule {q.Id} not found"));
        return Result<EscalationRuleDto>.Success(new EscalationRuleDto(r.Id, r.WorkflowProfileId, r.Name, r.TriggerAtSlaPercent, r.TriggerAtHoursBefore, r.ReassignToUserId, r.ReassignToDepartmentId, r.BumpPriority, r.NotifyAssignee, r.IsActive, r.CreatedAt, r.UpdatedAt));
    }
}

public sealed class CreateEscalationRuleCommandHandler(IEscalationRuleService svc) : IRequestHandler<CreateEscalationRuleCommand, Result<EscalationRuleDto>>
{
    public async Task<Result<EscalationRuleDto>> Handle(CreateEscalationRuleCommand cmd, CancellationToken ct)
    {
        var r = new TicketEscalationRule { WorkflowProfileId = cmd.WorkflowProfileId, Name = cmd.Name, TriggerAtSlaPercent = cmd.TriggerAtSlaPercent ?? 0, TriggerAtHoursBefore = cmd.TriggerAtHoursBefore ?? 0, ReassignToUserId = cmd.ReassignToUserId, ReassignToDepartmentId = cmd.ReassignToDepartmentId, BumpPriority = cmd.BumpPriority ?? false, NotifyAssignee = cmd.NotifyAssignee, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var created = await svc.CreateAsync(r, ct);
        return Result<EscalationRuleDto>.Success(new EscalationRuleDto(created.Id, created.WorkflowProfileId, created.Name, created.TriggerAtSlaPercent, created.TriggerAtHoursBefore, created.ReassignToUserId, created.ReassignToDepartmentId, created.BumpPriority, created.NotifyAssignee, created.IsActive, created.CreatedAt, created.UpdatedAt));
    }
}

public sealed class UpdateEscalationRuleCommandHandler(IEscalationRuleService svc) : IRequestHandler<UpdateEscalationRuleCommand, Result<EscalationRuleDto>>
{
    public async Task<Result<EscalationRuleDto>> Handle(UpdateEscalationRuleCommand cmd, CancellationToken ct)
    {
        var r = await svc.GetByIdAsync(cmd.Id, ct);
        if (r is null) return Result<EscalationRuleDto>.Failure(Error.NotFound($"Rule {cmd.Id} not found"));
        if (cmd.Name is not null) r.Name = cmd.Name;
        if (cmd.TriggerAtSlaPercent.HasValue) r.TriggerAtSlaPercent = cmd.TriggerAtSlaPercent.Value;
        if (cmd.TriggerAtHoursBefore.HasValue) r.TriggerAtHoursBefore = cmd.TriggerAtHoursBefore.Value;
        if (cmd.ReassignToUserId is not null) r.ReassignToUserId = cmd.ReassignToUserId;
        if (cmd.ReassignToDepartmentId is not null) r.ReassignToDepartmentId = cmd.ReassignToDepartmentId;
        if (cmd.BumpPriority.HasValue) r.BumpPriority = cmd.BumpPriority.Value;
        if (cmd.NotifyAssignee.HasValue) r.NotifyAssignee = cmd.NotifyAssignee.Value;
        if (cmd.IsActive.HasValue) r.IsActive = cmd.IsActive.Value;
        r.UpdatedAt = DateTime.UtcNow;
        await svc.UpdateAsync(r, ct);
        return Result<EscalationRuleDto>.Success(new EscalationRuleDto(r.Id, r.WorkflowProfileId, r.Name, r.TriggerAtSlaPercent, r.TriggerAtHoursBefore, r.ReassignToUserId, r.ReassignToDepartmentId, r.BumpPriority, r.NotifyAssignee, r.IsActive, r.CreatedAt, r.UpdatedAt));
    }
}

public sealed class DeleteEscalationRuleCommandHandler(IEscalationRuleService svc) : IRequestHandler<DeleteEscalationRuleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteEscalationRuleCommand cmd, CancellationToken ct)
    {
        await svc.DeleteAsync(cmd.Id, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
