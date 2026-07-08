using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.WorkflowProfiles.Commands;

public sealed record CreateWorkflowProfileCommand(Guid? ClientId, Guid DepartmentId, string Name, string? Description, int? SlaHours, Guid? SlaCalendarId, int? FirstResponseSlaHours, string? DefaultPriority) : ICommand<Result<WorkflowProfileDto>>;
public sealed record UpdateWorkflowProfileCommand(Guid Id, string? Name, string? Description, int? SlaHours, Guid? SlaCalendarId, int? FirstResponseSlaHours, string? DefaultPriority, bool? IsActive) : ICommand<Result<WorkflowProfileDto>>;
public sealed record DeleteWorkflowProfileCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record WorkflowProfileDto(Guid Id, Guid? ClientId, Guid DepartmentId, string Name, string? Description, int? SlaHours, Guid? SlaCalendarId, int? FirstResponseSlaHours, string? DefaultPriority, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);