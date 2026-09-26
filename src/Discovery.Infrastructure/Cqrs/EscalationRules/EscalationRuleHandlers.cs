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
        if (q.WorkflowProfileId.HasValue)
            rules = await svc.GetByWorkflowProfileIdAsync(q.WorkflowProfileId.Value, ct);
        else
            // Sem filtro: ativas de todos os perfis (antes retornava lista vazia).
            rules = await svc.GetAllActiveAsync(ct);
        return Result<IReadOnlyList<EscalationRuleDto>>.Success(rules.Select(Map).ToList().AsReadOnly());
    }
    private static EscalationRuleDto Map(TicketEscalationRule r) => new(r.Id, r.WorkflowProfileId, r.Name, r.TriggerAtSlaPercent, r.TriggerAtHoursBefore, r.ReassignToUserId, r.ReassignToDepartmentId, r.BumpPriority, r.NotifyAssignee, r.IsActive, r.CreatedAt, r.UpdatedAt, r.EscalationCooldownMinutes);
}

public sealed class GetEscalationRuleByIdQueryHandler(IEscalationRuleService svc) : IRequestHandler<GetEscalationRuleByIdQuery, Result<EscalationRuleDto>>
{
    public async Task<Result<EscalationRuleDto>> Handle(GetEscalationRuleByIdQuery q, CancellationToken ct)
    {
        var r = await svc.GetByIdAsync(q.Id, ct);
        if (r is null) return Result<EscalationRuleDto>.Failure(Error.NotFound($"Rule {q.Id} not found"));
        return Result<EscalationRuleDto>.Success(new EscalationRuleDto(r.Id, r.WorkflowProfileId, r.Name, r.TriggerAtSlaPercent, r.TriggerAtHoursBefore, r.ReassignToUserId, r.ReassignToDepartmentId, r.BumpPriority, r.NotifyAssignee, r.IsActive, r.CreatedAt, r.UpdatedAt, r.EscalationCooldownMinutes));
    }
}

public sealed class CreateEscalationRuleCommandHandler(IEscalationRuleService svc, IConfigurationAuditService audit) : IRequestHandler<CreateEscalationRuleCommand, Result<EscalationRuleDto>>
{
    public async Task<Result<EscalationRuleDto>> Handle(CreateEscalationRuleCommand cmd, CancellationToken ct)
    {
        var r = new TicketEscalationRule { WorkflowProfileId = cmd.WorkflowProfileId, Name = cmd.Name, TriggerAtSlaPercent = cmd.TriggerAtSlaPercent ?? 0, TriggerAtHoursBefore = cmd.TriggerAtHoursBefore ?? 0, ReassignToUserId = cmd.ReassignToUserId, ReassignToDepartmentId = cmd.ReassignToDepartmentId, BumpPriority = cmd.BumpPriority ?? false, NotifyAssignee = cmd.NotifyAssignee, IsActive = true, EscalationCooldownMinutes = cmd.EscalationCooldownMinutes is > 0 ? cmd.EscalationCooldownMinutes.Value : 360, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var created = await svc.CreateAsync(r, ct);
        await audit.LogChangeAsync("TicketEscalationRule", created.Id, "created", null, EscalationRuleAudit.Describe(created));
        return Result<EscalationRuleDto>.Success(new EscalationRuleDto(created.Id, created.WorkflowProfileId, created.Name, created.TriggerAtSlaPercent, created.TriggerAtHoursBefore, created.ReassignToUserId, created.ReassignToDepartmentId, created.BumpPriority, created.NotifyAssignee, created.IsActive, created.CreatedAt, created.UpdatedAt, created.EscalationCooldownMinutes));
    }
}

public sealed class UpdateEscalationRuleCommandHandler(IEscalationRuleService svc, IConfigurationAuditService audit) : IRequestHandler<UpdateEscalationRuleCommand, Result<EscalationRuleDto>>
{
    public async Task<Result<EscalationRuleDto>> Handle(UpdateEscalationRuleCommand cmd, CancellationToken ct)
    {
        var r = await svc.GetByIdAsync(cmd.Id, ct);
        if (r is null) return Result<EscalationRuleDto>.Failure(Error.NotFound($"Rule {cmd.Id} not found"));
        if (cmd.Name is not null) r.Name = cmd.Name;
        if (cmd.TriggerAtSlaPercent.HasValue) r.TriggerAtSlaPercent = cmd.TriggerAtSlaPercent.Value;
        if (cmd.TriggerAtHoursBefore.HasValue) r.TriggerAtHoursBefore = cmd.TriggerAtHoursBefore.Value;
        // "Clear*" distingue "não enviado" de "limpar o campo": sem isso não era
        // possível remover uma reatribuição já configurada.
        if (cmd.ClearReassignToUser == true) r.ReassignToUserId = null;
        else if (cmd.ReassignToUserId is not null) r.ReassignToUserId = cmd.ReassignToUserId;

        if (cmd.ClearReassignToDepartment == true) r.ReassignToDepartmentId = null;
        else if (cmd.ReassignToDepartmentId is not null) r.ReassignToDepartmentId = cmd.ReassignToDepartmentId;
        if (cmd.BumpPriority.HasValue) r.BumpPriority = cmd.BumpPriority.Value;
        if (cmd.NotifyAssignee.HasValue) r.NotifyAssignee = cmd.NotifyAssignee.Value;
        if (cmd.IsActive.HasValue) r.IsActive = cmd.IsActive.Value;
        if (cmd.EscalationCooldownMinutes is > 0) r.EscalationCooldownMinutes = cmd.EscalationCooldownMinutes.Value;
        r.UpdatedAt = DateTime.UtcNow;
        await svc.UpdateAsync(r, ct);
        await audit.LogChangeAsync("TicketEscalationRule", r.Id, "updated", null, EscalationRuleAudit.Describe(r));
        return Result<EscalationRuleDto>.Success(new EscalationRuleDto(r.Id, r.WorkflowProfileId, r.Name, r.TriggerAtSlaPercent, r.TriggerAtHoursBefore, r.ReassignToUserId, r.ReassignToDepartmentId, r.BumpPriority, r.NotifyAssignee, r.IsActive, r.CreatedAt, r.UpdatedAt, r.EscalationCooldownMinutes));
    }
}

/// <summary>Resumo legível da regra para a trilha de auditoria.</summary>
internal static class EscalationRuleAudit
{
    internal static string Describe(TicketEscalationRule r)
        => $"name={r.Name}; percent={r.TriggerAtSlaPercent}; horas={r.TriggerAtHoursBefore}; cooldown={r.EscalationCooldownMinutes}; ativo={r.IsActive}";
}

public sealed class DeleteEscalationRuleCommandHandler(IEscalationRuleService svc, IConfigurationAuditService audit) : IRequestHandler<DeleteEscalationRuleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteEscalationRuleCommand cmd, CancellationToken ct)
    {
        await svc.DeleteAsync(cmd.Id, ct);
        await audit.LogChangeAsync("TicketEscalationRule", cmd.Id, "deleted", cmd.Id.ToString(), null);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
