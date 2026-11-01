using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationTasks.Commands;
using Discovery.Core.Cqrs.AutomationTasks.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Discovery.Infrastructure.Cqrs.AutomationTasks;

public sealed class ListAutomationTasksQueryHandler(IAutomationTaskService svc) : IRequestHandler<ListAutomationTasksQuery, Result<CursorPageDto<AutomationTaskSummaryDto>>>
{
    public async Task<Result<CursorPageDto<AutomationTaskSummaryDto>>> Handle(ListAutomationTasksQuery q, CancellationToken ct)
    {
        // Escopo livre: quando scopeType/scopeId não forem informados, clientId pode
        // refinar tanto por escopo Client quanto apenas como filtro de coluna.
        var page = await svc.GetListPageAsync(
            q.ScopeType, q.ScopeId,
            activeOnly: q.ActiveOnly, deletedOnly: q.DeletedOnly, includeDeleted: q.IncludeDeleted,
            search: q.Search,
            clientId: q.ClientId,
            siteId: q.SiteId, agentId: q.AgentId,
            scopeTypes: q.ScopeTypes, actionTypes: q.ActionTypes, labels: q.Labels,
            triggers: q.Triggers, isActive: q.IsActive,
            q.Cursor, q.Limit, ct);

        return Result<CursorPageDto<AutomationTaskSummaryDto>>.Success(page);
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
        var dtos = items.Select(e =>
        {
            var (skipReason, decidedBy) = ExtractWingetDecision(e.ResultMetadataJson);
            return new AutomationTaskExecutionDto
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
                ErrorMessage = e.ErrorMessage,
                SkipReason = skipReason,
                DecidedBy = decidedBy
            };
        }).ToList();

        return Result<IReadOnlyList<AutomationTaskExecutionDto>>.Success(dtos);
    }

    /// <summary>
    /// Extrai a decisão do winget do metadata de resultado do agent
    /// (wingetDecision.skip/decidedBy). Permite medir quantas execuções foram
    /// puladas sem download e por qual fonte ("winget" × "inventory-cache").
    /// </summary>
    private static (string? SkipReason, string? DecidedBy) ExtractWingetDecision(string? resultMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(resultMetadataJson))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(resultMetadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("wingetDecision", out var decision)
                || decision.ValueKind != JsonValueKind.Object)
                return (null, null);

            var decidedBy = decision.TryGetProperty("decidedBy", out var decidedByElement)
                && decidedByElement.ValueKind == JsonValueKind.String
                    ? decidedByElement.GetString()
                    : null;

            var skipped = decision.TryGetProperty("skip", out var skipElement)
                && skipElement.ValueKind == JsonValueKind.True;
            var reason = skipped
                && decision.TryGetProperty("reason", out var reasonElement)
                && reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;

            return (reason, decidedBy);
        }
        catch (JsonException)
        {
            return (null, null);
        }
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
            AllowDefer = cmd.AllowDefer,
            CloseProcesses = cmd.CloseProcesses ?? (IReadOnlyList<string>)[],
            PromptTimeoutSeconds = cmd.PromptTimeoutSeconds,
            NotificationMode = cmd.NotificationMode,
            CollectorFieldDefinitionId = cmd.CollectorFieldDefinitionId,
            WriteOutputToNote = cmd.WriteOutputToNote ?? false,
            TaskRespectMaintenanceWindow = cmd.TaskRespectMaintenanceWindow,
            Key = cmd.Key,
            ToastTiming = cmd.ToastTiming,
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
/// Converte exceções de validação de domínio e violações de constraint do banco
/// em Error.Validation (HTTP 400) em vez de deixar estourar como 500.
///
/// Motivo (2026-10-31): a validação em C# da chave é uma janela de corrida — dois
/// POSTs simultâneos com a mesma key passam por ExistsKeyAsync e o segundo INSERT
/// estoura 23505 no índice único parcial (M254) → DbUpdateException → 500. O mesmo
/// vale para escopo excluído entre a validação e o INSERT (23503). Agora vira 400
/// com mensagem acionável.
/// </summary>
internal static class AutomationTaskValidationGuard
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string NotNullViolation = "23502";
    private const string CheckViolation = "23514";

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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return (default, Error.Validation("task", MapDatabaseError(pg)));
        }
    }

    private static string MapDatabaseError(PostgresException pg) => pg.SqlState switch
    {
        UniqueViolation when pg.ConstraintName?.Contains("scope_key", StringComparison.OrdinalIgnoreCase) == true
            => "Já existe uma tarefa ativa com esta chave neste escopo. Ajuste a chave (ou o nome) e tente novamente.",
        UniqueViolation
            => "Já existe uma tarefa com estes dados (violação de unicidade). Ajuste os valores e tente novamente.",
        ForeignKeyViolation
            => "Referência inválida: o cliente/site/agente (ou script) informado não existe mais.",
        NotNullViolation
            => $"Campo obrigatório não informado ({pg.ColumnName ?? "desconhecido"}).",
        CheckViolation
            => "Um dos valores informados viola uma regra do banco (check constraint).",
        _ => "Não foi possível salvar a tarefa por uma restrição do banco. Verifique os dados e tente novamente."
    };
}

public sealed class UpdateAutomationTaskCommandHandler(
    IAutomationTaskService svc,
    ISyncInvalidationPublisher syncPublisher) : IRequestHandler<UpdateAutomationTaskCommand, Result<AutomationTaskDetailDto>>
{
    public async Task<Result<AutomationTaskDetailDto>> Handle(UpdateAutomationTaskCommand cmd, CancellationToken ct)
    {
        // PUT parcial: os campos de notificacao ausentes preservam o valor atual
        // em vez de voltarem ao default do contrato (nao "zerar" a configuracao
        // de quem so mandou parte do payload).
        var current = await svc.GetByIdAsync(cmd.Id, includeInactive: true, cancellationToken: ct);
        if (current is null)
            return Result<AutomationTaskDetailDto>.Failure(Error.NotFound($"Task {cmd.Id} not found"));

        var request = new UpdateAutomationTaskRequest
        {
            // PUT parcial: TODO campo ausente preserva o valor atual. Antes, os
            // defaults do contrato (RunScript/Global/false) corrompiam a tarefa —
            // um PUT só com "description" virava RunScript ou policy GLOBAL,
            // apagando ClientId/SiteId/AgentId (revisão 2026-10-31, A03).
            Name = cmd.Name ?? current.Name,
            Description = cmd.Description ?? current.Description,
            ActionType = cmd.ActionType ?? current.ActionType,
            InstallationType = cmd.InstallationType ?? current.InstallationType,
            PackageId = cmd.PackageId ?? current.PackageId,
            ScriptId = cmd.ScriptId ?? current.ScriptId,
            CommandPayload = cmd.CommandPayload ?? current.CommandPayload,
            ScopeType = cmd.ScopeType ?? current.ScopeType,
            // Sem troca de escopo declarada, mantém o scopeId do registro; um
            // ScopeId explícito acompanha a troca de tipo de escopo.
            ScopeId = cmd.ScopeType.HasValue ? cmd.ScopeId : current.ScopeId,
            IncludeTags = cmd.IncludeTags ?? current.IncludeTags,
            ExcludeTags = cmd.ExcludeTags ?? current.ExcludeTags,
            TriggerImmediate = cmd.TriggerImmediate ?? current.TriggerImmediate,
            TriggerRecurring = cmd.TriggerRecurring ?? current.TriggerRecurring,
            TriggerOnUserLogin = cmd.TriggerOnUserLogin ?? current.TriggerOnUserLogin,
            TriggerOnAgentCheckIn = cmd.TriggerOnAgentCheckIn ?? current.TriggerOnAgentCheckIn,
            ScheduleCron = cmd.ScheduleCron ?? current.ScheduleCron,
            RequiresApproval = cmd.RequiresApproval ?? current.RequiresApproval,
            IsActive = cmd.IsActive ?? current.IsActive,
            AllowDefer = cmd.AllowDefer ?? current.AllowDefer,
            CloseProcesses = cmd.CloseProcesses ?? current.CloseProcesses,
            PromptTimeoutSeconds = cmd.PromptTimeoutSeconds ?? current.PromptTimeoutSeconds,
            NotificationMode = cmd.NotificationMode ?? current.NotificationMode,
            CollectorFieldDefinitionId = cmd.CollectorFieldDefinitionId ?? current.CollectorFieldDefinitionId,
            WriteOutputToNote = cmd.WriteOutputToNote ?? current.WriteOutputToNote,
            TaskRespectMaintenanceWindow = cmd.TaskRespectMaintenanceWindow ?? current.TaskRespectMaintenanceWindow,
            Key = cmd.Key ?? current.Key,
            ToastTiming = cmd.ToastTiming ?? current.ToastTiming,
            Reason = cmd.Reason
        };

        var (updated, updateError) = await AutomationTaskValidationGuard.TryAsync(async () => await svc.UpdateAsync(cmd.Id, request, cmd.ChangedBy, cmd.IpAddress, cmd.CorrelationId ?? "api", ct));
        if (updateError is not null) return Result<AutomationTaskDetailDto>.Failure(updateError);
        if (updated is null) return Result<AutomationTaskDetailDto>.Failure(Error.NotFound($"Task {cmd.Id} not found"));
        var result = Result<AutomationTaskDetailDto>.Success(updated);

        if (result.IsSuccess && cmd.TriggerImmediate == true)
        {
            // A11: usar o escopo EFETIVO da tarefa atualizada. Antes, um PUT parcial
            // (sem scopeType/scopeId) publicava invalidação com escopo Global/nulo.
            await syncPublisher.PublishByScopeAsync(
                SyncResourceType.AutomationPolicy,
                updated!.ScopeType,
                updated.ScopeId,
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
