using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AgentLabels.Queries;

public sealed record ListAgentLabelsQuery(Guid? AgentId) : IQuery<Result<IReadOnlyList<AgentLabelDto>>>;
public sealed record ListLabelRulesQuery(bool IncludeDisabled = true) : IQuery<Result<IReadOnlyList<LabelRuleDto>>>;
public sealed record GetLabelRuleByIdQuery(Guid Id) : IQuery<Result<LabelRuleDto>>;
public sealed record GetDistinctLabelsQuery : IQuery<Result<IReadOnlyList<string>>>;
public sealed record GetAvailableCustomFieldsQuery : IQuery<Result<IReadOnlyList<AvailableCustomFieldDto>>>;
public sealed record ListAgentsByRuleQuery(Guid RuleId) : IQuery<Result<AgentLabelRuleAgentsResponse>>;
public sealed record DryRunLabelRuleQuery(AgentLabelRuleDryRunRequest Request) : IQuery<Result<AgentLabelRuleDryRunResponse>>;

public sealed record AvailableCustomFieldDto(
    Guid Id, string Name, string FieldType, string? Description
);