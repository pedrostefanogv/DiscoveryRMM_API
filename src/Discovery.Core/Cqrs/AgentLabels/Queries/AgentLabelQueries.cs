using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AgentLabels.Queries;

public sealed record ListAgentLabelsQuery(Guid? AgentId) : IQuery<Result<IReadOnlyList<AgentLabelDto>>>;
public sealed record ListLabelRulesQuery(bool IncludeDisabled = true) : IQuery<Result<IReadOnlyList<LabelRuleDto>>>;
public sealed record GetLabelRuleByIdQuery(Guid Id) : IQuery<Result<LabelRuleDto>>;
public sealed record GetDistinctLabelsQuery : IQuery<Result<IReadOnlyList<string>>>;

/// <summary>Labels com contagem de agentes (alimenta o filtro da lista de agentes).</summary>
public sealed record GetLabelUsageQuery(int Limit = 200) : IQuery<Result<IReadOnlyList<AgentLabelUsageDto>>>;

/// <summary>Supressoes de labels de um agente.</summary>
public sealed record GetAgentLabelSuppressionsQuery(Guid AgentId) : IQuery<Result<IReadOnlyList<AgentLabelSuppressionDto>>>;

/// <summary>Ids de agentes que possuem uma label, com cursor.</summary>
public sealed record GetAgentIdsByLabelQuery(string Label, Guid? AfterAgentId, int Limit = 500) : IQuery<Result<AgentIdsByLabelResponse>>;

/// <summary>Busca as labels de varios agentes em uma unica chamada (evita N+1 na UI).</summary>
public sealed record ListAgentLabelsBatchQuery(IReadOnlyCollection<Guid> AgentIds) : IQuery<Result<IReadOnlyList<AgentLabelDto>>>;
public sealed record GetAvailableCustomFieldsQuery : IQuery<Result<IReadOnlyList<AvailableCustomFieldDto>>>;
public sealed record ListAgentsByRuleQuery(Guid RuleId, int Page = 1, int PageSize = 100) : IQuery<Result<AgentLabelRuleAgentsResponse>>;

/// <summary>Endpoint publico dos custom fields usaveis em regras (Agente, Site e Cliente).</summary>
public sealed record AvailableCustomFieldDto(
    Guid Id, string Name, string Label, string? Description, int ScopeType, int DataType, IReadOnlyList<string> Options
);
public sealed record DryRunLabelRuleQuery(AgentLabelRuleDryRunRequest Request) : IQuery<Result<AgentLabelRuleDryRunResponse>>;
public sealed record EvaluateLabelRuleImpactQuery(AgentLabelRuleImpactRequest Request) : IQuery<Result<AgentLabelRuleImpactResponse>>;