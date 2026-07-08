using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AutomationTasks.Queries;

public sealed record ListAutomationTasksQuery(Guid? ClientId, string? Cursor = null, int Limit = 50) : IQuery<Result<IReadOnlyList<AutomationTaskDto>>>;
public sealed record GetAutomationTaskByIdQuery(Guid Id) : IQuery<Result<AutomationTaskDto>>;

public sealed record AutomationTaskDto(Guid Id, string Name, string? Description, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);