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

public sealed class GetAutomationTaskByIdQueryHandler(IAutomationTaskService svc) : IRequestHandler<GetAutomationTaskByIdQuery, Result<AutomationTaskDetailDto>>
{
    public async Task<Result<AutomationTaskDetailDto>> Handle(GetAutomationTaskByIdQuery q, CancellationToken ct)
    {
        var t = await svc.GetByIdAsync(q.Id, false, ct);
        if (t is null) return Result<AutomationTaskDetailDto>.Failure(Error.NotFound($"Task {q.Id} not found"));
        return Result<AutomationTaskDetailDto>.Success(t);
    }
}

public sealed class GetAutomationTaskAuditQueryHandler(IAutomationTaskService svc) : IRequestHandler<GetAutomationTaskAuditQuery, Result<IReadOnlyList<AutomationTaskAuditDto>>>
{
    public async Task<Result<IReadOnlyList<AutomationTaskAuditDto>>> Handle(GetAutomationTaskAuditQuery q, CancellationToken ct)
    {
        var audit = await svc.GetAuditAsync(q.Id, q.Limit, ct);
        return Result<IReadOnlyList<AutomationTaskAuditDto>>.Success(audit);
    }
}

public sealed class GetAutomationTaskExecutionsQueryHandler(
    IAutomationTaskService taskService,
    IAutomationExecutionReportRepository reportRepo) : IRequestHandler<GetAutomationTaskExecutionsQuery, Result<IReadOnlyList<AutomationTaskExecutionDto>>>
{
    public async Task<Result<IReadOnlyList<AutomationTaskExecutionDto>>> Handle(GetAutomationTaskExecutionsQuery q, CancellationToken ct)
    {
        var task = await taskService.GetByIdAsync(q.Id, includeInactive: true, ct);
        if (task is null)
            return Result<IReadOnlyList<AutomationTaskExecutionDto>>.Failure(Error.NotFound($"Task {q.Id} not found"));

        var items = await reportRepo.GetByTaskIdAsync(q.Id, q.Limit);
        var dtos = items.Select(e => new AutomationTaskExecutionDto
        {
            Id = e.Id,
            CommandId = e.CommandId,
            AgentId = e.AgentId,
            SourceType = e.SourceType.ToString(),
            Status = e.Status.ToString(),
            CorrelationId = e.CorrelationId,
            CreatedAt = e.CreatedAt,
            AcknowledgedAt = e.AcknowledgedAt,
            ResultReceivedAt = e.ResultReceivedAt,
            ExitCode = e.ExitCode,
            ErrorMessage = e.ErrorMessage
        }).ToList();

        return Result<IReadOnlyList<AutomationTaskExecutionDto>>.Success(dtos);
    }
}

// ── Commands ─────────────────────────────────────────────────────────

public sealed class CreateAutomationTaskCommandHandler(
    IAutomationTaskService svc,
    ISyncInvalidationPublisher syncPublisher) : IRequestHandler<CreateAutomationTaskCommand, Result<AutomationTaskDetailDto>>
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

        var (created, createError) = await AutomationTaskValidationGuard.TryAsync(async () => await svc.CreateAsync(request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct));
        if (createError is not null) return Result<AutomationTaskDetailDto>.Failure(createError);
        var result = Result<AutomationTaskDetailDto>.Success(created!);
        if (result.IsSuccess && cmd.TriggerImmediate)
        {
            // Push imediato: agents do escopo fazem policy-sync em segundos em vez de esperar até 5 min.
            await syncPublisher.PublishByScopeAsync(
                SyncResourceType.AutomationPolicy,
                cmd.ScopeType,
                cmd.ScopeId,
                "automation-task-created-immediate",
                null,
                cmd.CorrelationId,
                ct);
        }

        return result;
    }
}

/// <summary>
/// Converte exceções de validação de domínio (InvalidOperationException do AutomationTaskService)
/// em Error.Validation (HTTP 400) em vez de deixar estourar como 500.
/// </summary>
internal static class AutomationTaskValidationGuard
{
    internal static async Task<(T? Value, Error? Error)> TryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return (await action().ConfigureAwait(false), null);
        }
        catch (InvalidOperationException ex)
        {
            return (default, Error.Validation("task", ex.Message));
        }
    }
}

public sealed class UpdateAutomationTaskCommandHandler(
    IAutomationTaskService svc,
    ISyncInvalidationPublisher syncPublisher) : IRequestHandler<UpdateAutomationTaskCommand, Result<AutomationTaskDetailDto>>
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

        var (updated, updateError) = await AutomationTaskValidationGuard.TryAsync(async () => await svc.UpdateAsync(cmd.Id, request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct));
        if (updateError is not null) return Result<AutomationTaskDetailDto>.Failure(updateError);
        if (updated is null) return Result<AutomationTaskDetailDto>.Failure(Error.NotFound($"Task {cmd.Id} not found"));
        var result = Result<AutomationTaskDetailDto>.Success(updated);

        if (result.IsSuccess && cmd.TriggerImmediate == true)
        {
            await syncPublisher.PublishByScopeAsync(
                SyncResourceType.AutomationPolicy,
                cmd.ScopeType ?? AppApprovalScopeType.Global,
                cmd.ScopeId,
                "automation-task-updated-immediate",
                null,
                cmd.CorrelationId,
                ct);
        }

        return result;
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
