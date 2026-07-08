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
        return Result<IReadOnlyList<WorkflowStateDto>>.Success(states.Select(s => new WorkflowStateDto(s.Id, s.ClientId, s.Name, s.Color, s.IsInitial, s.IsFinal, s.SortOrder, s.PausesSla, s.CreatedAt)).ToList().AsReadOnly());
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
        return Result<WorkflowStateDto>.Success(new WorkflowStateDto(s.Id, s.ClientId, s.Name, s.Color, s.IsInitial, s.IsFinal, s.SortOrder, s.PausesSla, s.CreatedAt));
    }
}

public sealed class CreateWorkflowStateCommandHandler(IWorkflowRepository repo) : IRequestHandler<CreateWorkflowStateCommand, Result<WorkflowStateDto>>
{
    public async Task<Result<WorkflowStateDto>> Handle(CreateWorkflowStateCommand cmd, CancellationToken ct)
    {
        var s = new CoreEntities.WorkflowState { ClientId = cmd.ClientId, Name = cmd.Name, Color = cmd.Color, IsInitial = cmd.IsInitial, IsFinal = cmd.IsFinal, SortOrder = cmd.SortOrder, PausesSla = cmd.PausesSla, CreatedAt = DateTime.UtcNow };
        var created = await repo.CreateStateAsync(s);
        return Result<WorkflowStateDto>.Success(new WorkflowStateDto(created.Id, created.ClientId, created.Name, created.Color, created.IsInitial, created.IsFinal, created.SortOrder, created.PausesSla, created.CreatedAt));
    }
}

public sealed class UpdateWorkflowStateCommandHandler(IWorkflowRepository repo) : IRequestHandler<UpdateWorkflowStateCommand, Result<WorkflowStateDto>>
{
    public async Task<Result<WorkflowStateDto>> Handle(UpdateWorkflowStateCommand cmd, CancellationToken ct)
    {
        var s = await repo.GetStateByIdAsync(cmd.Id);
        if (s is null) return Result<WorkflowStateDto>.Failure(Error.NotFound($"State {cmd.Id} not found"));
        if (cmd.Name is not null) s.Name = cmd.Name;
        if (cmd.Color is not null) s.Color = cmd.Color;
        if (cmd.IsInitial.HasValue) s.IsInitial = cmd.IsInitial.Value;
        if (cmd.IsFinal.HasValue) s.IsFinal = cmd.IsFinal.Value;
        if (cmd.SortOrder.HasValue) s.SortOrder = cmd.SortOrder.Value;
        if (cmd.PausesSla.HasValue) s.PausesSla = cmd.PausesSla.Value;
        await repo.UpdateStateAsync(s);
        return Result<WorkflowStateDto>.Success(new WorkflowStateDto(s.Id, s.ClientId, s.Name, s.Color, s.IsInitial, s.IsFinal, s.SortOrder, s.PausesSla, s.CreatedAt));
    }
}

public sealed class DeleteWorkflowStateCommandHandler(IWorkflowRepository repo) : IRequestHandler<DeleteWorkflowStateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteWorkflowStateCommand cmd, CancellationToken ct)
    {
        await repo.DeleteStateAsync(cmd.Id);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class CreateWorkflowTransitionCommandHandler(IWorkflowRepository repo) : IRequestHandler<CreateWorkflowTransitionCommand, Result<WorkflowTransitionDto>>
{
    public async Task<Result<WorkflowTransitionDto>> Handle(CreateWorkflowTransitionCommand cmd, CancellationToken ct)
    {
        var t = new CoreEntities.WorkflowTransition { ClientId = cmd.ClientId, FromStateId = cmd.FromStateId, ToStateId = cmd.ToStateId, Name = cmd.Name, CreatedAt = DateTime.UtcNow };
        var created = await repo.CreateTransitionAsync(t);
        return Result<WorkflowTransitionDto>.Success(new WorkflowTransitionDto(created.Id, created.ClientId, created.FromStateId, created.ToStateId, created.Name ?? string.Empty, created.CreatedAt));
    }
}

public sealed class DeleteWorkflowTransitionCommandHandler(IWorkflowRepository repo) : IRequestHandler<DeleteWorkflowTransitionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteWorkflowTransitionCommand cmd, CancellationToken ct)
    {
        await repo.DeleteTransitionAsync(cmd.Id);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
