using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.AutomationTasks.Queries;

public sealed record ListAutomationTasksQuery(
    Guid? ClientId = null,
    string? Cursor = null,
    int Limit = 50,
    Guid? SiteId = null,
    Guid? AgentId = null,
    AppApprovalScopeType? ScopeType = null,
    Guid? ScopeId = null,
    string? Search = null,
    IReadOnlyList<AppApprovalScopeType>? ScopeTypes = null,
    IReadOnlyList<AutomationTaskActionType>? ActionTypes = null,
    IReadOnlyList<string>? Labels = null,
    bool ActiveOnly = false,
    bool DeletedOnly = false,
    bool IncludeDeleted = false) : IQuery<Result<CursorPageDto<AutomationTaskSummaryDto>>>;

public sealed record GetAutomationTaskByIdQuery(Guid Id) : IQuery<Result<AutomationTaskDetailDto>>;
public sealed record GetAutomationTaskAuditQuery(Guid Id, int Limit = 50) : IQuery<Result<IReadOnlyList<AutomationTaskAuditDto>>>;
public sealed record GetAutomationTaskExecutionsQuery(Guid Id, int Limit = 50) : IQuery<Result<IReadOnlyList<AutomationTaskExecutionDto>>>;

[Obsolete("Substituído por AutomationTaskSummaryDto na listagem (não expõe escopo). Manter até v2.")]
public sealed record AutomationTaskDto(Guid Id, string Name, string? Description, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);