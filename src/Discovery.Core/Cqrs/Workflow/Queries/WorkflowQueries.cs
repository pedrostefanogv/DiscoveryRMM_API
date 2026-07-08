using Discovery.Core.Cqrs;
namespace Discovery.Core.Cqrs.Workflow.Queries;
public sealed record ListWorkflowStatesQuery(Guid ClientId) : IQuery<Result<List<WorkflowStateDto>>>;
public sealed record WorkflowStateDto(Guid Id, string Name, bool IsInitial, bool IsFinal, DateTime CreatedAt);
public sealed record ListWorkflowProfilesQuery(Guid? DepartmentId, string? Cursor, int Limit = 50) : IQuery<Result<ListProfilesResult>>;
public sealed record ListProfilesResult(IReadOnlyList<WorkflowProfileDto> Items, string? NextCursor, bool HasMore);
public sealed record WorkflowProfileDto(Guid Id, string Name, Guid? DepartmentId, DateTime CreatedAt);
