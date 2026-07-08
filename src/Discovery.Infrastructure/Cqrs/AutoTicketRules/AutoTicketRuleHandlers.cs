using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutoTicketRules.Commands;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutoTicketRules;

public sealed class ListAutoTicketRulesQueryHandler(IAutoTicketRuleRepository repo) : IRequestHandler<ListAutoTicketRulesQuery, Result<IReadOnlyList<AutoTicketRuleDto>>>
{
    public async Task<Result<IReadOnlyList<AutoTicketRuleDto>>> Handle(ListAutoTicketRulesQuery q, CancellationToken ct)
    {
        AutoTicketScopeLevel? sl = null; if (q.ScopeLevel is not null && Enum.TryParse<AutoTicketScopeLevel>(q.ScopeLevel, true, out var x)) sl = x;
        var rules = await repo.GetAllAsync(sl, q.ScopeId, q.IsEnabled);
        var items = rules.Select(Map).ToList().AsReadOnly();
        return Result<IReadOnlyList<AutoTicketRuleDto>>.Success(items);
    }
    private static AutoTicketRuleDto Map(AutoTicketRule r) => new(r.Id, r.Name, r.IsEnabled, r.ScopeLevel.ToString(), r.ScopeId, r.AlertCodeFilter, r.SourceFilter?.ToString(), r.TargetDepartmentId?.ToString(), r.TargetWorkflowProfileId?.ToString(), r.TargetCategory, r.TargetPriority.ToString(), r.DedupWindowMinutes, r.CooldownMinutes, r.CreatedAt, r.UpdatedAt);
}

public sealed class GetAutoTicketRuleByIdQueryHandler(IAutoTicketRuleRepository repo) : IRequestHandler<GetAutoTicketRuleByIdQuery, Result<AutoTicketRuleDto>>
{
    public async Task<Result<AutoTicketRuleDto>> Handle(GetAutoTicketRuleByIdQuery q, CancellationToken ct)
    {
        var r = await repo.GetByIdAsync(q.Id);
        if (r is null) return Result<AutoTicketRuleDto>.Failure(Error.NotFound($"Rule {q.Id} not found"));
        return Result<AutoTicketRuleDto>.Success(new AutoTicketRuleDto(r.Id, r.Name, r.IsEnabled, r.ScopeLevel.ToString(), r.ScopeId, r.AlertCodeFilter, r.SourceFilter?.ToString(), r.TargetDepartmentId?.ToString(), r.TargetWorkflowProfileId?.ToString(), r.TargetCategory, r.TargetPriority.ToString(), r.DedupWindowMinutes, r.CooldownMinutes, r.CreatedAt, r.UpdatedAt));
    }
}

public sealed class CreateAutoTicketRuleCommandHandler(IAutoTicketRuleRepository repo) : IRequestHandler<CreateAutoTicketRuleCommand, Result<AutoTicketRuleDto>>
{
    public async Task<Result<AutoTicketRuleDto>> Handle(CreateAutoTicketRuleCommand cmd, CancellationToken ct)
    {
        var r = new AutoTicketRule { Name = cmd.Name, IsEnabled = cmd.IsEnabled, ScopeLevel = Enum.TryParse<AutoTicketScopeLevel>(cmd.ScopeLevel, true, out var sl) ? sl : AutoTicketScopeLevel.Global, ScopeId = cmd.ScopeId, AlertCodeFilter = cmd.AlertCodeFilter, SourceFilter = null, TargetDepartmentId = Guid.TryParse(cmd.TargetDepartmentId, out var did) ? did : null, TargetWorkflowProfileId = Guid.TryParse(cmd.TargetWorkflowProfileId, out var pid) ? pid : null, TargetCategory = cmd.TargetCategory, TargetPriority = Enum.TryParse<TicketPriority>(cmd.TargetPriority, true, out var tp) ? tp : TicketPriority.Medium, DedupWindowMinutes = cmd.DedupWindowMinutes, CooldownMinutes = cmd.CooldownMinutes, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var created = await repo.CreateAsync(r);
        return Result<AutoTicketRuleDto>.Success(new AutoTicketRuleDto(created.Id, created.Name, created.IsEnabled, created.ScopeLevel.ToString(), created.ScopeId, created.AlertCodeFilter, null, created.TargetDepartmentId?.ToString(), created.TargetWorkflowProfileId?.ToString(), created.TargetCategory, created.TargetPriority.ToString(), created.DedupWindowMinutes, created.CooldownMinutes, created.CreatedAt, created.UpdatedAt));
    }
}

public sealed class UpdateAutoTicketRuleCommandHandler(IAutoTicketRuleRepository repo) : IRequestHandler<UpdateAutoTicketRuleCommand, Result<AutoTicketRuleDto>>
{
    public async Task<Result<AutoTicketRuleDto>> Handle(UpdateAutoTicketRuleCommand cmd, CancellationToken ct)
    {
        var r = await repo.GetByIdAsync(cmd.Id);
        if (r is null) return Result<AutoTicketRuleDto>.Failure(Error.NotFound($"Rule {cmd.Id} not found"));
        if (cmd.Name is not null) r.Name = cmd.Name;
        if (cmd.IsEnabled.HasValue) r.IsEnabled = cmd.IsEnabled.Value;
        if (cmd.ScopeLevel is not null && Enum.TryParse<AutoTicketScopeLevel>(cmd.ScopeLevel, true, out var sl)) r.ScopeLevel = sl;
        if (cmd.ScopeId is not null) r.ScopeId = cmd.ScopeId;
        if (cmd.AlertCodeFilter is not null) r.AlertCodeFilter = cmd.AlertCodeFilter;
        if (cmd.TargetDepartmentId is not null && Guid.TryParse(cmd.TargetDepartmentId, out var did)) r.TargetDepartmentId = did;
        if (cmd.TargetWorkflowProfileId is not null && Guid.TryParse(cmd.TargetWorkflowProfileId, out var pid)) r.TargetWorkflowProfileId = pid;
        if (cmd.TargetCategory is not null) r.TargetCategory = cmd.TargetCategory;
        if (cmd.TargetPriority is not null && Enum.TryParse<TicketPriority>(cmd.TargetPriority, true, out var tp)) r.TargetPriority = tp;
        if (cmd.DedupWindowMinutes.HasValue) r.DedupWindowMinutes = cmd.DedupWindowMinutes.Value;
        if (cmd.CooldownMinutes.HasValue) r.CooldownMinutes = cmd.CooldownMinutes.Value;
        r.UpdatedAt = DateTime.UtcNow;
        var updated = await repo.UpdateAsync(r);
        return Result<AutoTicketRuleDto>.Success(new AutoTicketRuleDto(updated.Id, updated.Name, updated.IsEnabled, updated.ScopeLevel.ToString(), updated.ScopeId, updated.AlertCodeFilter, updated.SourceFilter?.ToString(), updated.TargetDepartmentId?.ToString(), updated.TargetWorkflowProfileId?.ToString(), updated.TargetCategory, updated.TargetPriority.ToString(), updated.DedupWindowMinutes, updated.CooldownMinutes, updated.CreatedAt, updated.UpdatedAt));
    }
}

public sealed class DeleteAutoTicketRuleCommandHandler(IAutoTicketRuleRepository repo) : IRequestHandler<DeleteAutoTicketRuleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAutoTicketRuleCommand cmd, CancellationToken ct)
    {
        var ok = await repo.DeleteAsync(cmd.Id);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound($"Rule {cmd.Id} not found"));
    }
}
