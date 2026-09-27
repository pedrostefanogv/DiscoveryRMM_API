using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class GetTicketWatchersQueryHandler(ITicketWatcherRepository repo) : IRequestHandler<GetTicketWatchersQuery, Result<IEnumerable<TicketWatcher>>>
{ public async Task<Result<IEnumerable<TicketWatcher>>> Handle(GetTicketWatchersQuery q, CancellationToken ct) => Result<IEnumerable<TicketWatcher>>.Success(await repo.GetByTicketAsync(q.TicketId)); }

public sealed class GetTicketAutomationLinksQueryHandler(ITicketAutomationLinkRepository repo) : IRequestHandler<GetTicketAutomationLinksQuery, Result<IReadOnlyList<TicketAutomationLink>>>
{ public async Task<Result<IReadOnlyList<TicketAutomationLink>>> Handle(GetTicketAutomationLinksQuery q, CancellationToken ct) => Result<IReadOnlyList<TicketAutomationLink>>.Success(await repo.GetByTicketAsync(q.TicketId, ct)); }

public sealed class GetTicketKnowledgeLinksQueryHandler(ITicketKnowledgeLinkRepository repo) : IRequestHandler<GetTicketKnowledgeLinksQuery, Result<List<TicketKnowledgeLink>>>
{ public async Task<Result<List<TicketKnowledgeLink>>> Handle(GetTicketKnowledgeLinksQuery q, CancellationToken ct) => Result<List<TicketKnowledgeLink>>.Success(await repo.GetByTicketAsync(q.TicketId, ct)); }

/// <summary>
/// Timeline de auditoria do chamado. Resolve os GUIDs de responsável/estado/
/// departamento/chamado relacionado/máquina para nomes em consultas EM LOTE
/// (uma por entidade, sem N+1) e delega a descrição ao <see cref="TicketTimelineMapper"/>.
/// </summary>
public sealed class GetTicketAuditTimelineQueryHandler(
    ITicketActivityLogRepository repo,
    DiscoveryDbContext db)
    : IRequestHandler<GetTicketAuditTimelineQuery, Result<IReadOnlyList<TicketTimelineEntryDto>>>
{
    public async Task<Result<IReadOnlyList<TicketTimelineEntryDto>>> Handle(
        GetTicketAuditTimelineQuery q,
        CancellationToken ct)
    {
        var logs = await repo.GetByTicketAsync(q.TicketId);
        if (logs.Count == 0)
            return Result<IReadOnlyList<TicketTimelineEntryDto>>.Success(Array.Empty<TicketTimelineEntryDto>());

        var changedByIds = logs
            .Where(l => l.ChangedByUserId.HasValue)
            .Select(l => l.ChangedByUserId!.Value)
            .Distinct()
            .ToList();

        // (tipo, valor cru, GUID) — cada tipo aponta para uma entidade.
        var pending = new List<PendingValue>();
        foreach (var log in logs)
        {
            var kind = ResolveKind(log);
            if (kind == ValueKind.None) continue;

            foreach (var raw in new[] { log.OldValue, log.NewValue })
            {
                if (Guid.TryParse(raw, out var id))
                    pending.Add(new PendingValue(kind, raw, id));
            }
        }

        var nameByKindId = new Dictionary<(ValueKind, Guid), string>();

        // Usuários: quem alterou + old/new de atribuição/solicitante/IA.
        var userIds = changedByIds.ToHashSet();
        foreach (var item in pending)
        {
            if (item.Kind == ValueKind.User) userIds.Add(item.Id);
        }

        if (userIds.Count > 0)
        {
            var users = await db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.Login, u.Email })
                .ToListAsync(ct);

            foreach (var user in users)
                AddName(nameByKindId, ValueKind.User, user.Id, user.FullName ?? user.Login ?? user.Email);
        }

        var stateIds = IdsOf(pending, ValueKind.State);
        if (stateIds.Count > 0)
        {
            var states = await db.WorkflowStates.AsNoTracking()
                .Where(s => stateIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(ct);

            foreach (var state in states)
                AddName(nameByKindId, ValueKind.State, state.Id, state.Name);
        }

        var departmentIds = IdsOf(pending, ValueKind.Department);
        if (departmentIds.Count > 0)
        {
            var departments = await db.Departments.AsNoTracking()
                .Where(d => departmentIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Name })
                .ToListAsync(ct);

            foreach (var department in departments)
                AddName(nameByKindId, ValueKind.Department, department.Id, department.Name);
        }

        var ticketIds = IdsOf(pending, ValueKind.Ticket);
        if (ticketIds.Count > 0)
        {
            var tickets = await db.Tickets.AsNoTracking()
                .Where(t => ticketIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Title })
                .ToListAsync(ct);

            foreach (var ticket in tickets)
                AddName(nameByKindId, ValueKind.Ticket, ticket.Id, ticket.Title);
        }

        var agentIds = IdsOf(pending, ValueKind.Agent);
        if (agentIds.Count > 0)
        {
            var agents = await db.Agents.AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .Select(a => new { a.Id, a.DisplayName, a.Hostname })
                .ToListAsync(ct);

            foreach (var agent in agents)
                AddName(nameByKindId, ValueKind.Agent, agent.Id, agent.DisplayName ?? agent.Hostname);
        }

        var labelByValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pending)
        {
            if (labelByValue.ContainsKey(item.Raw)) continue;
            if (nameByKindId.TryGetValue((item.Kind, item.Id), out var name))
                labelByValue[item.Raw] = name;
        }

        var entries = logs
            // Feed: mais recente primeiro.
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .Select(log => TicketTimelineMapper.Map(
                log,
                log.ChangedByUserId.HasValue
                && nameByKindId.TryGetValue((ValueKind.User, log.ChangedByUserId.Value), out var name)
                    ? name
                    : null,
                labelByValue))
            .ToList();

        return Result<IReadOnlyList<TicketTimelineEntryDto>>.Success(entries);
    }

    private readonly record struct PendingValue(ValueKind Kind, string Raw, Guid Id);

    private static List<Guid> IdsOf(List<PendingValue> pending, ValueKind kind)
        => pending.Where(item => item.Kind == kind).Select(item => item.Id).Distinct().ToList();

    private static void AddName(
        Dictionary<(ValueKind, Guid), string> target,
        ValueKind kind,
        Guid id,
        string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) target[(kind, id)] = name;
    }

    private enum ValueKind { None, User, State, Department, Ticket, Agent }

    private static ValueKind ResolveKind(TicketActivityLog log) => log.Type switch
    {
        TicketActivityType.Assigned
            or TicketActivityType.RequesterChanged
            // newValue dos eventos de IA é o usuário escolhido.
            or TicketActivityType.AiAssigned
            or TicketActivityType.AiAssignmentSuggested => ValueKind.User,
        // StateChanged é reaproveitado para "workflow profile changed" — nesse
        // caso o valor é um perfil, então não resolvemos como estado.
        TicketActivityType.StateChanged when string.Equals(log.Comment, "Workflow profile changed", StringComparison.OrdinalIgnoreCase) => ValueKind.None,
        TicketActivityType.StateChanged => ValueKind.State,
        TicketActivityType.DepartmentChanged => ValueKind.Department,
        TicketActivityType.AgentChanged => ValueKind.Agent,
        TicketActivityType.TicketRelationAdded or TicketActivityType.TicketRelationRemoved => ValueKind.Ticket,
        _ => ValueKind.None
    };
}

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
            && filter.TemplateId is null
            && string.IsNullOrWhiteSpace(filter.AnswerKey)
            && string.IsNullOrWhiteSpace(filter.AnswerValue)
            && filter.AnswerMatch == TicketAnswerMatch.Exact
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
