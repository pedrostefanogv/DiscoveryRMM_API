using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationTasks.Commands;
using Discovery.Core.Cqrs.AutomationTasks.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutomationTasks;

public sealed class ListAutomationTasksQueryHandler(IAutomationTaskService svc) : IRequestHandler<ListAutomationTasksQuery, Result<CursorPageDto<AutomationTaskDto>>>
{
    public async Task<Result<CursorPageDto<AutomationTaskDto>>> Handle(ListAutomationTasksQuery q, CancellationToken ct)
    {
        // Mapeia clientId para os parâmetros do service
        Guid? scopeId = q.ClientId;
        AppApprovalScopeType? scopeType = q.ClientId.HasValue ? AppApprovalScopeType.Client : null;

        var page = await svc.GetListPageAsync(
            scopeType, scopeId,
            activeOnly: true, deletedOnly: false, includeDeleted: false,
            search: null,
            clientId: q.ClientId,
            siteId: null, agentId: null,
            scopeTypes: null, actionTypes: null, labels: null,
            q.Cursor, q.Limit, ct);

        var dtos = page.Items.Select(t => new AutomationTaskDto(t.Id, t.Name, t.Description, t.IsActive, t.LastUpdatedAt, t.LastUpdatedAt)).ToList();
        return Result<CursorPageDto<AutomationTaskDto>>.Success(
            new CursorPageDto<AutomationTaskDto>(dtos.AsReadOnly(), dtos.Count, page.Cursor, page.NextCursor, page.HasMore, page.Limit));
    }
}

public sealed class GetAutomationTaskByIdQueryHandler(IAutomationTaskService svc) : IRequestHandler<GetAutomationTaskByIdQuery, Result<AutomationTaskDto>>
{
    public async Task<Result<AutomationTaskDto>> Handle(GetAutomationTaskByIdQuery q, CancellationToken ct)
    {
        var t = await svc.GetByIdAsync(q.Id, false, ct);
        if (t is null) return Result<AutomationTaskDto>.Failure(Error.NotFound($"Task {q.Id} not found"));
        return Result<AutomationTaskDto>.Success(new AutomationTaskDto(t.Id, t.Name, t.Description, t.IsActive, t.CreatedAt, t.UpdatedAt));
    }
}

// ── Commands ─────────────────────────────────────────────────────────

public sealed class CreateAutomationTaskCommandHandler(IAutomationTaskService svc) : IRequestHandler<CreateAutomationTaskCommand, Result<AutomationTaskDetailDto>>
{
    public async Task<Result<AutomationTaskDetailDto>> Handle(CreateAutomationTaskCommand cmd, CancellationToken ct)
    {
        var request = new CreateAutomationTaskRequest
        {
            Name = cmd.Name,
            Description = cmd.Description,
            ActionType = cmd.ActionType,
            InstallationType = cmd.InstallationType,
            PackageId = cmd.PackageId,
            ScriptId = cmd.ScriptId,
            CommandPayload = cmd.CommandPayload,
            ScopeType = cmd.ScopeType,
            ScopeId = cmd.ScopeId,
            IncludeTags = cmd.IncludeTags,
            ExcludeTags = cmd.ExcludeTags,
            TriggerImmediate = cmd.TriggerImmediate,
            TriggerRecurring = cmd.TriggerRecurring,
            TriggerOnUserLogin = cmd.TriggerOnUserLogin,
            TriggerOnAgentCheckIn = cmd.TriggerOnAgentCheckIn,
            ScheduleCron = cmd.ScheduleCron,
            RequiresApproval = cmd.RequiresApproval,
            IsActive = cmd.IsActive
        };

        var result = await svc.CreateAsync(request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct);
        return Result<AutomationTaskDetailDto>.Success(result);
    }
}

public sealed class UpdateAutomationTaskCommandHandler(IAutomationTaskService svc) : IRequestHandler<UpdateAutomationTaskCommand, Result<AutomationTaskDetailDto>>
{
    public async Task<Result<AutomationTaskDetailDto>> Handle(UpdateAutomationTaskCommand cmd, CancellationToken ct)
    {
        var request = new UpdateAutomationTaskRequest
        {
            Name = cmd.Name ?? string.Empty,
            Description = cmd.Description,
            ActionType = cmd.ActionType ?? AutomationTaskActionType.RunScript,
            InstallationType = cmd.InstallationType,
            PackageId = cmd.PackageId,
            ScriptId = cmd.ScriptId,
            CommandPayload = cmd.CommandPayload,
            ScopeType = cmd.ScopeType ?? AppApprovalScopeType.Global,
            ScopeId = cmd.ScopeId,
            IncludeTags = cmd.IncludeTags ?? (IReadOnlyList<string>)[],
            ExcludeTags = cmd.ExcludeTags ?? (IReadOnlyList<string>)[],
            TriggerImmediate = cmd.TriggerImmediate ?? false,
            TriggerRecurring = cmd.TriggerRecurring ?? false,
            TriggerOnUserLogin = cmd.TriggerOnUserLogin ?? false,
            TriggerOnAgentCheckIn = cmd.TriggerOnAgentCheckIn ?? false,
            ScheduleCron = cmd.ScheduleCron,
            RequiresApproval = cmd.RequiresApproval ?? false,
            IsActive = cmd.IsActive ?? true,
            Reason = cmd.Reason
        };

        var result = await svc.UpdateAsync(cmd.Id, request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct);
        if (result is null) return Result<AutomationTaskDetailDto>.Failure(Error.NotFound($"Task {cmd.Id} not found"));
        return Result<AutomationTaskDetailDto>.Success(result);
    }
}

public sealed class DeleteAutomationTaskCommandHandler(IAutomationTaskService svc) : IRequestHandler<DeleteAutomationTaskCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAutomationTaskCommand cmd, CancellationToken ct)
    {
        var ok = await svc.DeleteAsync(cmd.Id, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", cmd.Reason, ct);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound($"Task {cmd.Id} not found"));
    }
}

public sealed class RestoreAutomationTaskCommandHandler(IAutomationTaskService svc) : IRequestHandler<RestoreAutomationTaskCommand, Result<AutomationTaskDetailDto>>
{
    public async Task<Result<AutomationTaskDetailDto>> Handle(RestoreAutomationTaskCommand cmd, CancellationToken ct)
    {
        var result = await svc.RestoreAsync(cmd.Id, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", cmd.Reason, ct);
        if (result is null) return Result<AutomationTaskDetailDto>.Failure(Error.NotFound($"Task {cmd.Id} not found"));
        return Result<AutomationTaskDetailDto>.Success(result);
    }
}
