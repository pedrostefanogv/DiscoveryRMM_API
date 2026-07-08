using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AutoTicketRules.Commands;

public sealed record CreateAutoTicketRuleCommand(string Name, bool IsEnabled, string ScopeLevel, Guid? ScopeId, string? AlertCodeFilter, string? SourceFilter, string TargetDepartmentId, string? TargetWorkflowProfileId, string? TargetCategory, string TargetPriority, int DedupWindowMinutes, int CooldownMinutes) : ICommand<Result<AutoTicketRuleDto>>;
public sealed record UpdateAutoTicketRuleCommand(Guid Id, string? Name, bool? IsEnabled, string? ScopeLevel, Guid? ScopeId, string? AlertCodeFilter, string? SourceFilter, string? TargetDepartmentId, string? TargetWorkflowProfileId, string? TargetCategory, string? TargetPriority, int? DedupWindowMinutes, int? CooldownMinutes) : ICommand<Result<AutoTicketRuleDto>>;
public sealed record DeleteAutoTicketRuleCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record AutoTicketRuleDto(Guid Id, string Name, bool IsEnabled, string ScopeLevel, Guid? ScopeId, string? AlertCodeFilter, string? SourceFilter, string? TargetDepartmentId, string? TargetWorkflowProfileId, string? TargetCategory, string? TargetPriority, int DedupWindowMinutes, int CooldownMinutes, DateTime CreatedAt, DateTime UpdatedAt);