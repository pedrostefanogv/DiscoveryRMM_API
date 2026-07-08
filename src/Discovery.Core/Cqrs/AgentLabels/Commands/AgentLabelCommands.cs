using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentLabels.Commands;

public sealed record AddAgentLabelCommand(Guid AgentId, string Label) : ICommand<Result<AgentLabelDto>>;
public sealed record RemoveAgentLabelCommand(Guid LabelId) : ICommand<Result<VoidResult>>;
public sealed record CreateLabelRuleCommand(string Name, string Label, string? Description, bool IsEnabled, string ApplyMode, string ExpressionJson, string? CreatedBy) : ICommand<Result<LabelRuleDto>>;
public sealed record UpdateLabelRuleCommand(Guid Id, string? Name, string? Label, string? Description, bool? IsEnabled, string? ApplyMode, string? ExpressionJson, string? UpdatedBy) : ICommand<Result<LabelRuleDto>>;
public sealed record DeleteLabelRuleCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record AgentLabelDto(Guid Id, Guid AgentId, string Label, string SourceType, DateTime CreatedAt);
public sealed record LabelRuleDto(Guid Id, string Name, string Label, string? Description, bool IsEnabled, string ApplyMode, string ExpressionJson, string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);