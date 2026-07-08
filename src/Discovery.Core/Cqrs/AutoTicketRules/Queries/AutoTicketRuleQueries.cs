using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutoTicketRules.Commands;

namespace Discovery.Core.Cqrs.AutoTicketRules.Queries;

public sealed record ListAutoTicketRulesQuery(string? ScopeLevel, Guid? ScopeId, bool? IsEnabled) : IQuery<Result<IReadOnlyList<AutoTicketRuleDto>>>;
public sealed record GetAutoTicketRuleByIdQuery(Guid Id) : IQuery<Result<AutoTicketRuleDto>>;