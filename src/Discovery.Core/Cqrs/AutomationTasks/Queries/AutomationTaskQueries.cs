using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AutomationTasks.Queries;

public sealed record ListAutomationTasksQuery(Guid? ClientId, string? Cursor = null, int Limit = 50) : IQuery<Result<CursorPageDto<AutomationTaskDto>>>;
public sealed record GetAutomationTaskByIdQuery(Guid Id) : IQuery<Result<AutomationTaskDetailDto>>;
public sealed record GetAutomationTaskAuditQuery(Guid Id, int Limit = 50) : IQuery<Result<IReadOnlyList<AutomationTaskAuditDto>>>;

public sealed record AutomationTaskDto(Guid Id, string Name, string? Description, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);