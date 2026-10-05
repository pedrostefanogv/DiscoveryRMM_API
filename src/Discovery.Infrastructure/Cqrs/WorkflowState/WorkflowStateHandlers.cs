using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.WorkflowState.Commands;
using Discovery.Core.Cqrs.WorkflowState.Queries;
using CoreEntities = Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.WorkflowState;

public sealed class ListWorkflowStatesQueryHandler(IWorkflowRepository repo) : IRequestHandler<ListWorkflowStatesQuery, Result<IReadOnlyList<WorkflowStateDto>>>
{
    public async Task<Result<IReadOnlyList<WorkflowStateDto>>> Handle(ListWorkflowStatesQuery q, CancellationToken ct)
    {
        var states = await repo.GetStatesAsync(q.ClientId);
        return Result<IReadOnlyList<WorkflowStateDto>>.Success(states.Select(WorkflowMapping.MapState).ToList().AsReadOnly());
    }
}

public sealed class ListWorkflowTransitionsQueryHandler(IWorkflowRepository repo) : IRequestHandler<ListWorkflowTransitionsQuery, Result<IReadOnlyList<WorkflowTransitionDto>>>
{
    public async Task<Result<IReadOnlyList<WorkflowTransitionDto>>> Handle(ListWorkflowTransitionsQuery q, CancellationToken ct)
    {
        var trans = await repo.GetTransitionsAsync(q.ClientId);
        return Result<IReadOnlyList<WorkflowTransitionDto>>.Success(trans.Select(t => new WorkflowTransitionDto(t.Id, t.ClientId, t.FromStateId, t.ToStateId, t.Name ?? string.Empty, t.CreatedAt)).ToList().AsReadOnly());
    }
}

public sealed class GetWorkflowStateByIdQueryHandler(IWorkflowRepository repo) : IRequestHandler<GetWorkflowStateByIdQuery, Result<WorkflowStateDto>>
{
    public async Task<Result<WorkflowStateDto>> Handle(GetWorkflowStateByIdQuery q, CancellationToken ct)
    {
        var s = await repo.GetStateByIdAsync(q.Id);
        if (s is null) return Result<WorkflowStateDto>.Failure(Error.NotFound($"State {q.Id} not found"));
        return Result<WorkflowStateDto>.Success(WorkflowMapping.MapState(s));
    }
}

public sealed class CreateWorkflowStateCommandHandler(
    IWorkflowRepository repo,
    IConfigurationAuditService? audit = null) : IRequestHandler<CreateWorkflowStateCommand, Result<WorkflowStateDto>>
{
    public async Task<Result<WorkflowStateDto>> Handle(CreateWorkflowStateCommand cmd, CancellationToken ct)
    {
        var name = cmd.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return Result<WorkflowStateDto>.Failure(Error.Validation("Name", "O nome do estado é obrigatório."));
        if (cmd.IsInitial && cmd.IsFinal)
            return Result<WorkflowStateDto>.Failure(Error.Validation("IsFinal", "Um estado não pode ser inicial e final ao mesmo tempo."));
        if (cmd.SortOrder < 0)
            return Result<WorkflowStateDto>.Failure(Error.Validation("SortOrder", "A ordem deve ser maior ou igual a zero."));
        if (cmd.IsInitial && await repo.HasInitialStateAsync(cmd.ClientId))
            return Result<WorkflowStateDto>.Failure(Error.Validation("IsInitial", "Já existe um estado inicial para este escopo."));

        var s = new CoreEntities.WorkflowState
        {
            ClientId = cmd.ClientId,
            Name = name,
            Color = WorkflowMapping.NormalizeColor(cmd.Color),
            IsInitial = cmd.IsInitial,
            IsFinal = cmd.IsFinal,
            SortOrder = cmd.SortOrder,
            PausesSla = cmd.PausesSla,
            CreatedAt = DateTime.UtcNow
        };
        var created = await repo.CreateStateAsync(s);
        if (audit is not null)
            await audit.LogChangeAsync("WorkflowState", created.Id, "created", null, WorkflowMapping.DescribeState(created));
        return Result<WorkflowStateDto>.Success(WorkflowMapping.MapState(created));
    }
}

public sealed class UpdateWorkflowStateCommandHandler(
    IWorkflowRepository repo,
    IConfigurationAuditService? audit = null) : IRequestHandler<UpdateWorkflowStateCommand, Result<WorkflowStateDto>>
{
    public async Task<Result<WorkflowStateDto>> Handle(UpdateWorkflowStateCommand cmd, CancellationToken ct)
    {
        var s = await repo.GetStateByIdAsync(cmd.Id);
        if (s is null) return Result<WorkflowStateDto>.Failure(Error.NotFound($"State {cmd.Id} not found"));

        // PUT (semântica de substituição): Name, quando enviado, não pode ser vazio.
        if (cmd.Name is not null && string.IsNullOrWhiteSpace(cmd.Name))
            return Result<WorkflowStateDto>.Failure(Error.Validation("Name", "O nome do estado não pode ser vazio."));
        if (cmd.SortOrder.HasValue && cmd.SortOrder.Value < 0)
            return Result<WorkflowStateDto>.Failure(Error.Validation("SortOrder", "A ordem deve ser maior ou igual a zero."));

        var effectiveInitial = cmd.IsInitial ?? s.IsInitial;
        var effectiveFinal = cmd.IsFinal ?? s.IsFinal;
        if (effectiveInitial && effectiveFinal)
            return Result<WorkflowStateDto>.Failure(Error.Validation("IsFinal", "Um estado não pode ser inicial e final ao mesmo tempo."));
        if (cmd.IsInitial == true && !s.IsInitial
            && await repo.HasInitialStateAsync(s.ClientId, excludeId: s.Id))
            return Result<WorkflowStateDto>.Failure(Error.Validation("IsInitial", "Já existe um estado inicial para este escopo."));

        var oldDescription = WorkflowMapping.DescribeState(s);

        if (cmd.Name is not null) s.Name = cmd.Name.Trim();
        // Color nulo/vazio LIMPA a cor; um valor informado sobrescreve.
        if (cmd.Color is not null) s.Color = WorkflowMapping.NormalizeColor(cmd.Color);
        if (cmd.IsInitial.HasValue) s.IsInitial = cmd.IsInitial.Value;
        if (cmd.IsFinal.HasValue) s.IsFinal = cmd.IsFinal.Value;
        if (cmd.SortOrder.HasValue) s.SortOrder = cmd.SortOrder.Value;
        if (cmd.PausesSla.HasValue) s.PausesSla = cmd.PausesSla.Value;

        await repo.UpdateStateAsync(s);
        if (audit is not null)
            await audit.LogChangeAsync("WorkflowState", s.Id, "updated", oldDescription, WorkflowMapping.DescribeState(s));
        return Result<WorkflowStateDto>.Success(WorkflowMapping.MapState(s));
    }
}

public sealed class DeleteWorkflowStateCommandHandler(
    IWorkflowRepository repo,
    IConfigurationAuditService? audit = null) : IRequestHandler<DeleteWorkflowStateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteWorkflowStateCommand cmd, CancellationToken ct)
    {
        var existing = await repo.GetStateByIdAsync(cmd.Id);
        if (existing is null)
            return Result<VoidResult>.Failure(Error.NotFound($"State {cmd.Id} not found"));

        var ticketCount = await repo.CountTicketsInStateAsync(cmd.Id);
        if (ticketCount > 0)
            return Result<VoidResult>.Failure(Error.Validation("Id",
                $"Existem {ticketCount} chamados neste estado. Mova-os antes de excluir."));

        await repo.DeleteStateAsync(cmd.Id);
        if (audit is not null)
            await audit.LogChangeAsync("WorkflowState", cmd.Id, "deleted", WorkflowMapping.DescribeState(existing), null);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class CreateWorkflowTransitionCommandHandler(
    IWorkflowRepository repo,
    IConfigurationAuditService? audit = null) : IRequestHandler<CreateWorkflowTransitionCommand, Result<WorkflowTransitionDto>>
{
    public async Task<Result<WorkflowTransitionDto>> Handle(CreateWorkflowTransitionCommand cmd, CancellationToken ct)
    {
        if (cmd.FromStateId == cmd.ToStateId)
            return Result<WorkflowTransitionDto>.Failure(Error.Validation("ToStateId", "A origem e o destino não podem ser o mesmo estado."));

        if (await repo.GetStateByIdAsync(cmd.FromStateId) is null)
            return Result<WorkflowTransitionDto>.Failure(Error.Validation("FromStateId", "O estado de origem não existe."));
        if (await repo.GetStateByIdAsync(cmd.ToStateId) is null)
            return Result<WorkflowTransitionDto>.Failure(Error.Validation("ToStateId", "O estado de destino não existe."));

        if (await repo.TransitionExistsAsync(cmd.FromStateId, cmd.ToStateId, cmd.ClientId))
            return Result<WorkflowTransitionDto>.Failure(Error.Validation("ToStateId", "Já existe uma transição entre esses estados neste escopo."));

        var t = new CoreEntities.WorkflowTransition
        {
            ClientId = cmd.ClientId,
            FromStateId = cmd.FromStateId,
            ToStateId = cmd.ToStateId,
            Name = cmd.Name,
            CreatedAt = DateTime.UtcNow
        };
        var created = await repo.CreateTransitionAsync(t);
        if (audit is not null)
            await audit.LogChangeAsync("WorkflowTransition", created.Id, "created", null, WorkflowMapping.DescribeTransition(created));
        return Result<WorkflowTransitionDto>.Success(new WorkflowTransitionDto(created.Id, created.ClientId, created.FromStateId, created.ToStateId, created.Name ?? string.Empty, created.CreatedAt));
    }
}

public sealed class DeleteWorkflowTransitionCommandHandler(
    IWorkflowRepository repo,
    IConfigurationAuditService? audit = null) : IRequestHandler<DeleteWorkflowTransitionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteWorkflowTransitionCommand cmd, CancellationToken ct)
    {
        await repo.DeleteTransitionAsync(cmd.Id);
        if (audit is not null)
            await audit.LogChangeAsync("WorkflowTransition", cmd.Id, "deleted", cmd.Id.ToString(), null);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

/// <summary>Mapeamento/resumo legível usado nos DTOs e na trilha de auditoria.</summary>
internal static class WorkflowMapping
{
    internal static WorkflowStateDto MapState(CoreEntities.WorkflowState s)
        => new(s.Id, s.ClientId, s.Name, s.Color, s.IsInitial, s.IsFinal, s.SortOrder, s.PausesSla, s.CreatedAt);

    internal static string? NormalizeColor(string? color)
        => string.IsNullOrWhiteSpace(color) ? null : color.Trim();

    internal static string DescribeState(CoreEntities.WorkflowState s)
        => $"name={s.Name}; initial={s.IsInitial}; final={s.IsFinal}; pausesSla={s.PausesSla}; order={s.SortOrder}; color={s.Color ?? "none"}; clientId={s.ClientId?.ToString() ?? "global"}";

    internal static string DescribeTransition(CoreEntities.WorkflowTransition t)
        => $"from={t.FromStateId}; to={t.ToStateId}; name={t.Name ?? "none"}; clientId={t.ClientId?.ToString() ?? "global"}";
}
