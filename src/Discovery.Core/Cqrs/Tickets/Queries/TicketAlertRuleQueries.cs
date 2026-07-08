using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Tickets.Queries;

public sealed record ListTicketAlertRulesQuery : IQuery<Result<IReadOnlyList<TicketAlertRule>>>;
public sealed record GetTicketAlertRuleByIdQuery(Guid Id) : IQuery<Result<TicketAlertRule>>;
public sealed record GetTicketAlertRulesByWorkflowStateQuery(Guid WorkflowStateId) : IQuery<Result<IReadOnlyList<TicketAlertRule>>>;
