using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class GetTicketWatchersQueryHandler(ITicketWatcherRepository repo) : IRequestHandler<GetTicketWatchersQuery, Result<IEnumerable<TicketWatcher>>>
{ public async Task<Result<IEnumerable<TicketWatcher>>> Handle(GetTicketWatchersQuery q, CancellationToken ct) => Result<IEnumerable<TicketWatcher>>.Success(await repo.GetByTicketAsync(q.TicketId)); }

public sealed class GetTicketRemoteSessionsQueryHandler(ITicketRemoteSessionRepository repo) : IRequestHandler<GetTicketRemoteSessionsQuery, Result<IEnumerable<TicketRemoteSession>>>
{ public async Task<Result<IEnumerable<TicketRemoteSession>>> Handle(GetTicketRemoteSessionsQuery q, CancellationToken ct) => Result<IEnumerable<TicketRemoteSession>>.Success(await repo.GetByTicketAsync(q.TicketId, ct)); }

public sealed class GetTicketAutomationLinksQueryHandler(ITicketAutomationLinkRepository repo) : IRequestHandler<GetTicketAutomationLinksQuery, Result<IReadOnlyList<TicketAutomationLink>>>
{ public async Task<Result<IReadOnlyList<TicketAutomationLink>>> Handle(GetTicketAutomationLinksQuery q, CancellationToken ct) => Result<IReadOnlyList<TicketAutomationLink>>.Success(await repo.GetByTicketAsync(q.TicketId, ct)); }

public sealed class GetTicketKnowledgeLinksQueryHandler(ITicketKnowledgeLinkRepository repo) : IRequestHandler<GetTicketKnowledgeLinksQuery, Result<List<TicketKnowledgeLink>>>
{ public async Task<Result<List<TicketKnowledgeLink>>> Handle(GetTicketKnowledgeLinksQuery q, CancellationToken ct) => Result<List<TicketKnowledgeLink>>.Success(await repo.GetByTicketAsync(q.TicketId, ct)); }

public sealed class GetTicketAuditTimelineQueryHandler(ITicketActivityLogRepository repo) : IRequestHandler<GetTicketAuditTimelineQuery, Result<List<TicketActivityLog>>>
{ public async Task<Result<List<TicketActivityLog>>> Handle(GetTicketAuditTimelineQuery q, CancellationToken ct) => Result<List<TicketActivityLog>>.Success(await repo.GetByTicketAsync(q.TicketId)); }

public sealed class GetTicketKpiQueryHandler(ITicketKpiCacheService kpiCache, ITicketRepository ticketRepo) : IRequestHandler<GetTicketKpiQuery, Result<TicketKpiResult>>
{
    public async Task<Result<TicketKpiResult>> Handle(GetTicketKpiQuery q, CancellationToken ct)
    {
        var result = await kpiCache.GetOrComputeAsync(q.ClientId, q.DepartmentId, q.Since, () => ticketRepo.GetKpiAsync(q.ClientId, q.DepartmentId, q.Since), ct);
        return Result<TicketKpiResult>.Success(result);
    }
}
