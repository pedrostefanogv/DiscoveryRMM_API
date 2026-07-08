using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class ListTicketAlertRulesQueryHandler(ITicketAlertRuleRepository repo) : IRequestHandler<ListTicketAlertRulesQuery, Result<IReadOnlyList<TicketAlertRule>>>
{ public async Task<Result<IReadOnlyList<TicketAlertRule>>> Handle(ListTicketAlertRulesQuery q, CancellationToken ct) => Result<IReadOnlyList<TicketAlertRule>>.Success((await repo.GetAllAsync()).ToList()); }

public sealed class GetTicketAlertRuleByIdQueryHandler(ITicketAlertRuleRepository repo) : IRequestHandler<GetTicketAlertRuleByIdQuery, Result<TicketAlertRule>>
{
    public async Task<Result<TicketAlertRule>> Handle(GetTicketAlertRuleByIdQuery q, CancellationToken ct)
    { var r = await repo.GetByIdAsync(q.Id); return r is null ? Result<TicketAlertRule>.Failure(Error.NotFound("Alert rule not found.")) : Result<TicketAlertRule>.Success(r); }
}

public sealed class GetTicketAlertRulesByWorkflowStateQueryHandler(ITicketAlertRuleRepository repo) : IRequestHandler<GetTicketAlertRulesByWorkflowStateQuery, Result<IReadOnlyList<TicketAlertRule>>>
{ public async Task<Result<IReadOnlyList<TicketAlertRule>>> Handle(GetTicketAlertRulesByWorkflowStateQuery q, CancellationToken ct) => Result<IReadOnlyList<TicketAlertRule>>.Success((await repo.GetByWorkflowStateIdAsync(q.WorkflowStateId)).ToList()); }
