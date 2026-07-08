using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.EscalationRules.Commands;

public sealed record CreateEscalationRuleCommand(Guid WorkflowProfileId, string Name, int? TriggerAtSlaPercent, int? TriggerAtHoursBefore, Guid? ReassignToUserId, Guid? ReassignToDepartmentId, bool? BumpPriority, bool NotifyAssignee) : ICommand<Result<EscalationRuleDto>>;
public sealed record UpdateEscalationRuleCommand(Guid Id, string? Name, int? TriggerAtSlaPercent, int? TriggerAtHoursBefore, Guid? ReassignToUserId, Guid? ReassignToDepartmentId, bool? BumpPriority, bool? NotifyAssignee, bool? IsActive) : ICommand<Result<EscalationRuleDto>>;
public sealed record DeleteEscalationRuleCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record EscalationRuleDto(Guid Id, Guid WorkflowProfileId, string Name, int? TriggerAtSlaPercent, int? TriggerAtHoursBefore, Guid? ReassignToUserId, Guid? ReassignToDepartmentId, bool? BumpPriority, bool NotifyAssignee, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);