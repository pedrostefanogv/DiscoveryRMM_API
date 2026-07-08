using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.WorkflowState.Commands;

namespace Discovery.Core.Cqrs.WorkflowState.Queries;

public sealed record ListWorkflowStatesQuery(Guid? ClientId) : IQuery<Result<IReadOnlyList<WorkflowStateDto>>>;
public sealed record ListWorkflowTransitionsQuery(Guid? ClientId) : IQuery<Result<IReadOnlyList<WorkflowTransitionDto>>>;
public sealed record GetWorkflowStateByIdQuery(Guid Id) : IQuery<Result<WorkflowStateDto>>;