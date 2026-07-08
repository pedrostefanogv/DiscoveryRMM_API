using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Workflow.Queries;
namespace Discovery.Core.Cqrs.Workflow.Commands;
public sealed record CreateWorkflowStateCommand(Guid ClientId, string Name, bool IsInitial, bool IsFinal) : ICommand<Result<WorkflowStateDto>>;
public sealed record UpdateWorkflowStateCommand(Guid Id, string Name) : ICommand<Result<WorkflowStateDto>>;
