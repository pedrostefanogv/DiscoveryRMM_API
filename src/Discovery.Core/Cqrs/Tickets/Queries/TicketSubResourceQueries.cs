using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Tickets.Queries;

// ── Watchers ─────────────────────────────────────────────────────────────
public sealed record GetTicketWatchersQuery(Guid TicketId) : IQuery<Result<IEnumerable<TicketWatcher>>>;

// ── Remote Sessions ──────────────────────────────────────────────────────
public sealed record GetTicketRemoteSessionsQuery(Guid TicketId) : IQuery<Result<IEnumerable<TicketRemoteSession>>>;

// ── Automation Links ─────────────────────────────────────────────────────
public sealed record GetTicketAutomationLinksQuery(Guid TicketId) : IQuery<Result<IReadOnlyList<TicketAutomationLink>>>;

// ── Knowledge Links ──────────────────────────────────────────────────────
public sealed record GetTicketKnowledgeLinksQuery(Guid TicketId) : IQuery<Result<List<TicketKnowledgeLink>>>;

/// <summary>Sugestões de artigos da KB para um ticket: busca híbrida (semântica + keyword)
/// com ACL do usuário. Sem q, deriva o texto do título/descrição do ticket.</summary>
public sealed record SuggestTicketKnowledgeQuery(
    Guid TicketId,
    string? Query = null,
    Guid? ClientId = null,
    Guid? SiteId = null,
    Guid? DepartmentId = null,
    int MaxResults = 5) : IQuery<Result<IReadOnlyList<ArticleResponse>>>;

// ── Audit Timeline ───────────────────────────────────────────────────────
public sealed record GetTicketAuditTimelineQuery(Guid TicketId) : IQuery<Result<List<TicketActivityLog>>>;

// ── KPI ──────────────────────────────────────────────────────────────────
public sealed record GetTicketKpiQuery(Guid? ClientId, Guid? DepartmentId, DateTime? Since) : IQuery<Result<TicketKpiResult>>;
