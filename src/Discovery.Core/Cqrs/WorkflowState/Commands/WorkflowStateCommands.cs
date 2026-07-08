using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.WorkflowState.Commands;

public sealed record CreateWorkflowStateCommand(Guid? ClientId, string Name, string? Color, bool IsInitial, bool IsFinal, int SortOrder, bool PausesSla) : ICommand<Result<WorkflowStateDto>>;
public sealed record UpdateWorkflowStateCommand(Guid Id, string? Name, string? Color, bool? IsInitial, bool? IsFinal, int? SortOrder, bool? PausesSla) : ICommand<Result<WorkflowStateDto>>;
public sealed record DeleteWorkflowStateCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record CreateWorkflowTransitionCommand(Guid? ClientId, Guid FromStateId, Guid ToStateId, string Name) : ICommand<Result<WorkflowTransitionDto>>;
public sealed record DeleteWorkflowTransitionCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record WorkflowStateDto(Guid Id, Guid? ClientId, string Name, string? Color, bool IsInitial, bool IsFinal, int SortOrder, bool PausesSla, DateTime CreatedAt);
public sealed record WorkflowTransitionDto(Guid Id, Guid? ClientId, Guid FromStateId, Guid ToStateId, string Name, DateTime CreatedAt);