using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.EscalationRules.Commands;

namespace Discovery.Core.Cqrs.EscalationRules.Queries;

public sealed record ListEscalationRulesQuery(Guid? WorkflowProfileId) : IQuery<Result<IReadOnlyList<EscalationRuleDto>>>;
public sealed record GetEscalationRuleByIdQuery(Guid Id) : IQuery<Result<EscalationRuleDto>>;