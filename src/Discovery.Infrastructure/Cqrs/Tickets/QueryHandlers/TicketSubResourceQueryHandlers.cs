using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
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

public sealed class GetTicketKpiQueryHandler(
    ITicketKpiCacheService kpiCache,
    ITicketRepository ticketRepo,
    IScopeContext scopeContext) : IRequestHandler<GetTicketKpiQuery, Result<TicketKpiResult>>
{
    public async Task<Result<TicketKpiResult>> Handle(GetTicketKpiQuery q, CancellationToken ct)
    {
        // Row-level security: injeta o ACL do usuário no filtro.
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        var filter = q.Filter with
        {
            HasGlobalAccess = access.HasGlobalAccess,
            AllowedClientIds = access.AllowedClientIds,
            AllowedSiteIds = access.AllowedSiteIds
        };

        // Cache só é seguro para o recorte simples (sem filtros avançados e com acesso global).
        var isSimple = access.HasGlobalAccess
            && filter.SiteId is null
            && filter.AgentId is null
            && filter.WorkflowProfileId is null
            && filter.WorkflowStateId is null
            && filter.AssignedToUserId is null
            && filter.Priority is null
            && filter.SlaBreached is null
            && filter.IsClosed is null
            && string.IsNullOrWhiteSpace(filter.Text);

        var result = isSimple
            ? await kpiCache.GetOrComputeAsync(
                filter.ClientId, filter.DepartmentId, filter.Since,
                () => ticketRepo.GetKpiAsync(filter), ct)
            : await ticketRepo.GetKpiAsync(filter);

        return Result<TicketKpiResult>.Success(result);
    }
}

/// <summary>
/// Sugestões de artigos da KB para um ticket: busca híbrida (semântica + keyword)
/// limitada ao escopo do ticket (client/site) + ACL do usuário. O texto da busca
/// pode vir da UI (q) ou ser derivado do título + descrição do ticket.
/// </summary>
public sealed class SuggestTicketKnowledgeQueryHandler(ITicketRepository ticketRepo, IKnowledgeSearchService searchService)
    : IRequestHandler<SuggestTicketKnowledgeQuery, Result<IReadOnlyList<ArticleResponse>>>
{
    public async Task<Result<IReadOnlyList<ArticleResponse>>> Handle(SuggestTicketKnowledgeQuery q, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        if (ticket is null)
            return Result<IReadOnlyList<ArticleResponse>>.Failure(Error.NotFound($"Ticket {q.TicketId} not found."));

        var query = string.IsNullOrWhiteSpace(q.Query)
            ? $"{ticket.Title} {ticket.Description}".Trim()
            : q.Query.Trim();
        if (query.Length > 500) query = query[..500];

        var clientId = q.ClientId ?? (Guid?)ticket.ClientId;
        var siteId = q.SiteId ?? ticket.SiteId;
        var departmentId = q.DepartmentId ?? ticket.DepartmentId;

        var hits = await searchService.SearchAsync(new KnowledgeSearchRequest(
            query, "hybrid",
            UseUserScope: !clientId.HasValue && !siteId.HasValue,
            clientId, siteId, departmentId,
            q.MaxResults <= 0 ? 5 : Math.Min(q.MaxResults, 20)), ct);

        var dtos = hits.Select(h => MapToResponse(h.Article)).ToList();
        return Result<IReadOnlyList<ArticleResponse>>.Success(dtos);
    }

    private static ArticleResponse MapToResponse(KnowledgeArticle a) => new(
        Id: a.Id, Title: a.Title, Content: a.Content, Category: a.Category,
        Tags: ParseTags(a.TagsJson), CreatedBy: a.CreatedBy, LastEditedBy: a.LastEditedBy,
        LastEditedAt: a.LastEditedAt, Status: a.Status,
        Scope: ResolveScope(a.ClientId, a.SiteId), ScopeOrigin: ResolveScopeOrigin(a.ClientId, a.SiteId),
        ClientId: a.ClientId, SiteId: a.SiteId, ClientName: null, SiteName: null,
        DepartmentId: a.DepartmentId, CurrentVersionNumber: a.CurrentVersionNumber,
        PublishedAt: a.PublishedAt, ChunkCount: a.Chunks?.Count ?? 0,
        EmbeddingsReady: a.LastChunkedAt.HasValue,
        CreatedAt: a.CreatedAt, UpdatedAt: a.UpdatedAt);

    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ResolveScope(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string ResolveScopeOrigin(Guid? clientId, Guid? siteId)
        => (clientId, siteId) switch
        {
            (null, null) => "global",
            (not null, null) => "client",
            _ => "site"
        };
}
